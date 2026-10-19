// Copyright (c) 2026 smallketchup82 <ketchup@smkt.ca>. Licensed under the MIT License.
// See the LICENSE file in the repository root for full license text.

using Spectre.Console;

namespace TempFold.Firmware;

internal static class FirmwareUpdate
{
    internal static async Task<int> RunAsync(ArduinoTools tools, string port, string? listen, CancellationToken token)
    {
        AnsiConsole.Write(new Rule("TFirm configuration"));
        FirmwareConfiguration saved;
        using (var serial = new FirmwareSerial(port))
        {
            using var response = await serial.RequestAsync("GET", "", token);
            if (response.RootElement.GetProperty("restartRequired").GetBoolean())
                throw new InvalidDataException("The board has pending settings. Restart it before opening the update panel.");
            saved = FirmwareConfiguration.Read(response.RootElement);
        }
        var edited = saved;
        string? password = null;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            Show(saved, edited, password, port);
            bool connected = HostNetwork.LocalAddresses().Length > 0;
            var actions = new List<string> { "Board name", "Wi-Fi SSID", "Wi-Fi password", "Server address", "Sampling / USB / HTTP interval", "Sensor wiring", "Disable networking (USB-only)" };
            if (connected) actions.Add("Populate from this computer's network");
            else AnsiConsole.MarkupLine("[grey]Network population disabled: no connected LAN address. Connect Ethernet/Wi-Fi or enter settings manually.[/]");
            actions.AddRange(["Flash", "Cancel"]);
            string action = FirmwareInstaller.Choose("Edit settings, then Flash to save over USB", actions.ToArray());
            switch (action)
            {
                case "Cancel": return 130;
                case "Board name": edited = edited with { BoardName = FirmwareInstaller.AskText("Board name:", 64, edited.BoardName) }; break;
                case "Wi-Fi SSID": edited = edited with { WifiSsid = Text("Wi-Fi SSID (blank allowed in USB-only mode):", edited.WifiSsid, 32) }; break;
                case "Wi-Fi password":
                    string choice = FirmwareInstaller.Choose("Password", "Preserve stored password", "Replace password", "Clear password (open network)");
                    password = choice switch
                    {
                        "Preserve stored password" => null,
                        "Clear password (open network)" => "",
                        _ => AnsiConsole.Prompt(new TextPrompt<string>("New Wi-Fi password:").Secret().AllowEmpty()
                            .Validate(p => FirmwareValidation.ValidPassword(p) ? ValidationResult.Success() : ValidationResult.Error("Use 8–63 UTF-8 bytes, 64 hex characters, or blank.")))
                    };
                    break;
                case "Server address": edited = edited with { ApiEndpoint = Endpoint(edited.ApiEndpoint) }; break;
                case "Sampling / USB / HTTP interval":
                    edited = edited with { SensorIntervalMs = Number("Common sampling and USB output interval in milliseconds (HTTP receives each new sample):", edited.SensorIntervalMs, n => n is >= 1000 and <= 3600000) }; break;
                case "Sensor wiring":
                    int sda = Number("SDA GPIO:", edited.SdaPin, Pin);
                    int scl = Number("SCL GPIO (different from SDA):", edited.SclPin, n => Pin(n) && n != sda);
                    int address = Number("Sensor I2C address (68 = 0x44, 69 = 0x45):", edited.SensorAddress, n => n is 68 or 69);
                    edited = edited with { SdaPin = sda, SclPin = scl, SensorAddress = address }; break;
                case "Disable networking (USB-only)": edited = edited with { ApiEndpoint = "" }; break;
                case "Populate from this computer's network":
                    edited = await PopulateAsync(edited, listen, token, p => password = p); break;
                case "Flash":
                    try { edited.Validate(password); }
                    catch (InvalidDataException ex) { AnsiConsole.MarkupLine("[red]" + Markup.Escape(ex.Message) + "[/]"); continue; }
                    if (!AnsiConsole.Confirm("Persist these edited settings and restart the selected board?", false)) continue;
                    return await ApplyAsync(tools, port, saved, edited, password, token);
            }
        }
    }

    private static bool Pin(int n) => n is 0 or 1 or >= 3 and <= 10 or 20 or 21;
    private static int Number(string title, int initial, Func<int, bool> valid) => AnsiConsole.Prompt(new TextPrompt<int>(title).DefaultValue(initial)
        .Validate(n => valid(n) ? ValidationResult.Success() : ValidationResult.Error("Value is outside the supported range.")));
    private static string Text(string title, string initial, int maximum) => AnsiConsole.Prompt(new TextPrompt<string>(title).AllowEmpty().DefaultValue(initial)
        .Validate(s => FirmwareValidation.ValidText(s, maximum) ? ValidationResult.Success() : ValidationResult.Error($"Use at most {maximum} UTF-8 bytes, without control characters.")));
    private static string Endpoint(string initial) => AnsiConsole.Prompt(new TextPrompt<string>("HTTP server base URL (blank disables networking):").AllowEmpty().DefaultValue(initial)
        .Validate(s => s.Length == 0 || FirmwareValidation.ValidEndpoint(s) ? ValidationResult.Success() : ValidationResult.Error("Use http://LAN-IP-or-domain:port with no path or trailing slash.")));

    private static void Show(FirmwareConfiguration saved, FirmwareConfiguration edited, string? password, string port)
    {
        var table = new Table().Title(Markup.Escape(port)).AddColumn("Setting").AddColumn("Saved").AddColumn("Edited");
        void Row(string label, string before, string after) => table.AddRow(label, Markup.Escape(before), before == after ? Markup.Escape(after) : "[yellow]" + Markup.Escape(after) + "[/]");
        Row("Name", saved.BoardName, edited.BoardName);
        Row("SSID", saved.WifiSsid, edited.WifiSsid);
        Row("Password", saved.WifiPasswordSet ? "Stored (hidden)" : "None", password is null ? (saved.WifiPasswordSet ? "Stored (hidden)" : "None") : password.Length == 0 ? "Clear" : "Replace (hidden)");
        Row("Server / mode", saved.ApiEndpoint.Length == 0 ? "USB-only" : saved.ApiEndpoint, edited.ApiEndpoint.Length == 0 ? "USB-only" : edited.ApiEndpoint);
        Row("Sampling / USB / HTTP interval", saved.SensorIntervalMs + " ms", edited.SensorIntervalMs + " ms");
        Row("SDA / SCL / address", $"{saved.SdaPin} / {saved.SclPin} / {saved.SensorAddress}", $"{edited.SdaPin} / {edited.SclPin} / {edited.SensorAddress}");
        AnsiConsole.Write(table);
    }

    private static async Task<FirmwareConfiguration> PopulateAsync(FirmwareConfiguration edited, string? listen, CancellationToken token, Action<string> setPassword)
    {
        edited = edited with { ApiEndpoint = FirmwareInstaller.AskEndpoint(listen) };
        try
        {
            var network = await HostNetwork.CopyWifiAsync(token);
            if (!FirmwareValidation.ValidText(network.Ssid, 32) || network.Ssid.Length == 0 || !FirmwareValidation.ValidPassword(network.Password))
                throw new IOException("The OS returned unsupported Wi-Fi settings.");
            edited = edited with { WifiSsid = network.Ssid };
            setPassword(network.Password);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            AnsiConsole.MarkupLine("[yellow]" + Markup.Escape(ex.Message) + "[/]");
            AnsiConsole.MarkupLine("The LAN server address is populated. Ethernet supplies no Wi-Fi credentials. Select a saved Wi-Fi profile if available, or enter credentials manually.");
            var profiles = await HostNetwork.SavedWifiProfilesAsync(token);
            if (profiles.Length > 0)
            {
                string selected = FirmwareInstaller.Choose("Optional saved Wi-Fi profile", profiles.Select(p => p.Label).Append("Enter manually / keep existing").ToArray());
                var profile = profiles.FirstOrDefault(p => p.Label == selected);
                if (profile is not null)
                {
                    try
                    {
                        var network = await HostNetwork.ReadSavedWifiAsync(profile.Id, token);
                        if (!FirmwareValidation.ValidText(network.Ssid, 32) || network.Ssid.Length == 0 || !FirmwareValidation.ValidPassword(network.Password))
                            throw new IOException("The saved profile contains unsupported Wi-Fi settings.");
                        edited = edited with { WifiSsid = network.Ssid };
                        setPassword(network.Password);
                    }
                    catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or NotSupportedException)
                    { AnsiConsole.MarkupLine("[yellow]" + Markup.Escape(failure.Message) + " Enter credentials manually.[/]"); }
                }
            }
        }
        return edited;
    }

    private static async Task<int> ApplyAsync(ArduinoTools tools, string port, FirmwareConfiguration saved, FirmwareConfiguration edited, string? password, CancellationToken token)
    {
        using (var serial = new FirmwareSerial(port))
        {
            using var current = await serial.RequestAsync("GET", "", token);
            if (current.RootElement.GetProperty("restartRequired").GetBoolean() || FirmwareConfiguration.Read(current.RootElement) != saved)
                throw new InvalidDataException("The board configuration changed while editing. Reopen the panel before applying changes.");
            using var response = await serial.RequestAsync("SET", " " + edited.Patch(saved, password), token);
            using var restart = await serial.RequestAsync("RESTART", "", token);
        }
        await Task.Delay(2000, token);
        port = await FirmwareInstaller.ReconnectPortAsync(tools, port, token);
        using var verified = new FirmwareSerial(port);
        using var readback = await verified.RequestAsync("GET", "", token);
        if (readback.RootElement.GetProperty("restartRequired").GetBoolean()) throw new InvalidDataException("The board still requires a restart.");
        edited.Verify(FirmwareConfiguration.Read(readback.RootElement), password);
        AnsiConsole.MarkupLine("[green]Configuration persisted and verified.[/]");
        try
        {
            double temperature = await FirmwareInstaller.Busy("Waiting for the next sensor sample…", () => verified.TemperatureAsync(token, TimeSpan.FromMilliseconds(edited.SensorIntervalMs + 15000)));
            AnsiConsole.MarkupLine($"[green]Sensor responding:[/] {temperature:F2} °C");
        }
        catch (IOException ex) { AnsiConsole.MarkupLine("[yellow]Configuration is saved; sensor verification incomplete: " + Markup.Escape(ex.Message) + "[/]"); return 2; }
        if (edited.ApiEndpoint.Length > 0)
        {
            var delivery = await FirmwareInstaller.Busy("Checking Wi-Fi connectivity and HTTP delivery…", async () =>
            {
                bool wifi = false;
                int http = 0;
                long deadline = Environment.TickCount64 + 60000;
                do
                {
                    using var status = await verified.RequestAsync("GET", "", token);
                    wifi = status.RootElement.GetProperty("wifiConnected").GetBoolean();
                    http = status.RootElement.GetProperty("lastHttpStatus").GetInt32();
                    if (wifi && http is >= 200 and < 300) break;
                    await Task.Delay(2000, token);
                } while (Environment.TickCount64 < deadline);
                return (Wifi: wifi, Http: http);
            });
            AnsiConsole.MarkupLine($"Wi-Fi: {(delivery.Wifi ? "connected" : "not connected")}; server delivery: {(delivery.Http is >= 200 and < 300 ? "confirmed" : "unconfirmed")} (HTTP status {delivery.Http}).");
            if (!delivery.Wifi || delivery.Http is < 200 or >= 300) { AnsiConsole.MarkupLine("[yellow]Settings are saved. Check Wi-Fi, LAN listener, firewall and server availability.[/]"); return 2; }
        }
        return 0;
    }
}
