// Copyright (c) 2026 smallketchup82 <ketchup@smkt.ca>. Licensed under the MIT License.
// See the LICENSE file in the repository root for full license text.

using System.Text.Json;
using Spectre.Console;

namespace TempFold.Firmware;

internal static class FirmwareInstaller
{
    internal static Task<int> RunAsync(string repository, string? listen, CancellationToken token) =>
        WithWorkspaceAsync(async workspace =>
        {
            AnsiConsole.Write(new Rule("TempFold • ESP32-C3 setup"));
            string? source = null;
            string sourceChoice = Choose("Firmware source", "Download the latest precompiled firmware", "Compile the latest firmware locally", "Use a local firmware file");
            bool compileLocally = sourceChoice == "Compile the latest firmware locally";
            if (sourceChoice == "Download the latest precompiled firmware")
            {
                try { source = await Busy("Looking for the latest TFirm release…", () => FirmwareImage.DownloadLatestAsync(repository, workspace, token)); }
                catch (Exception ex) when (ex is HttpRequestException or IOException)
                { AnsiConsole.MarkupLine("[yellow]Download unavailable:[/] " + Markup.Escape(ex.Message)); }
                if (source is null) AnsiConsole.MarkupLine("[yellow]No downloadable firmware found. Private repositories require a local download.[/]");
            }
            FirmwareImage? image = null;
            while (!compileLocally)
            {
                source ??= AnsiConsole.Prompt(new TextPrompt<string>("Path to a TFirm [green].tar.gz[/] release or [green]merged .bin[/]:")
                    .Validate(p => File.Exists(p) ? ValidationResult.Success() : ValidationResult.Error("File not found.")));
                try { image = await FirmwareImage.LoadAsync(source, workspace, token); break; }
                catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or KeyNotFoundException)
                { AnsiConsole.MarkupLine("[red]Invalid firmware:[/] " + Markup.Escape(ex.Message)); source = null; }
            }
            var tools = await PrepareToolsAsync(workspace, token);
            if (compileLocally)
            {
                try
                {
                    string compiled = await Busy("Downloading source and compiling TFirm…", () => tools.CompileLatestAsync(repository, workspace, token));
                    image = await FirmwareImage.LoadAsync(compiled, workspace, token);
                }
                catch (Exception ex) when (ex is IOException or HttpRequestException)
                {
                    AnsiConsole.MarkupLine("[yellow]Local compilation unavailable:[/] " + Markup.Escape(ex.Message));
                    string local = AnsiConsole.Prompt(new TextPrompt<string>("Path to a locally built merged .bin or release .tar.gz:")
                        .Validate(p => File.Exists(p) ? ValidationResult.Success() : ValidationResult.Error("File not found.")));
                    image = await FirmwareImage.LoadAsync(local, workspace, token);
                }
            }
            if (image is null) throw new InvalidOperationException("No firmware selected.");
            string port = await SelectPortAsync(tools, token);
            const string name = "TempFold sensor";
            var settings = new DeviceSettings(name);
            AnsiConsole.Write(new Table().AddColumn("Setting").AddColumn("Value")
                .AddRow("Firmware", Markup.Escape(image.Version ?? "Local merged image"))
                .AddRow("Device", Markup.Escape(port)).AddRow("Name", Markup.Escape(name))
                .AddRow("Mode", "USB-only"));
            AnsiConsole.MarkupLine("Stop TempFold and any serial monitors using this device. Initial flashing replaces its firmware and saved settings.");
            if (!AnsiConsole.Confirm("Flash and configure this ESP32-C3?", false)) return 130;
            while (true)
            {
                try { await Busy("Flashing and verifying firmware…", async () => { await tools.FlashAsync(image, port, workspace, token); return true; }); break; }
                catch (IOException ex)
                {
                    AnsiConsole.MarkupLine("[red]" + Markup.Escape(ex.Message) + "[/]");
                    AnsiConsole.MarkupLine("If needed, hold BOOT, tap RESET, release BOOT, then select its current port. Check the USB cable and close other serial applications.");
                    if (!AnsiConsole.Confirm("Retry flashing?", true)) return 1;
                    port = await SelectPortAsync(tools, token);
                }
            }
            await Task.Delay(2000, token);
            port = await ReconnectPortAsync(tools, port, token);
            using (var serial = new FirmwareSerial(port))
            {
                await Task.Delay(1500, token);
                using var initial = await serial.RequestAsync("GET", "", token);
                ValidateDevice(initial.RootElement, image.Version);
                using var saved = await serial.RequestAsync("RESET", "", token);
                using var restarted = await serial.RequestAsync("RESTART", "", token);
            }
            await Task.Delay(2000, token);
            port = await ReconnectPortAsync(tools, port, token);
            using (var serial = new FirmwareSerial(port))
            {
                await Task.Delay(1500, token);
                using var verified = await serial.RequestAsync("GET", "", token);
                ValidateDevice(verified.RootElement, image.Version);
                ValidateSettings(verified.RootElement, settings);
                try
                {
                    double temperature = await Busy("Waiting for a sensor reading…", () => serial.TemperatureAsync(token));
                    AnsiConsole.MarkupLine($"[green]Sensor responding:[/] {temperature:F2} °C");
                }
                catch (IOException ex) { AnsiConsole.MarkupLine("[yellow]" + Markup.Escape(ex.Message) + "[/]"); return 2; }
            }
            AnsiConsole.MarkupLine("[green]Setup complete.[/] " + Markup.Escape(name));
            AnsiConsole.MarkupLine("USB port: " + Markup.Escape(port));
            if (AnsiConsole.Confirm("Open the firmware update panel for this board?", false))
                return await FirmwareUpdate.RunAsync(tools, port, listen, token);
            return 0;
        });

    internal static async Task<int> WithWorkspaceAsync(Func<string, Task<int>> action)
    {
        string workspace = Directory.CreateTempSubdirectory("tempfold-firmware-").FullName;
        try
        {
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(workspace, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return await action(workspace);
        }
        finally { CleanupWorkspace(workspace); }
    }

    private static void CleanupWorkspace(string workspace)
    {
        try { Directory.Delete(workspace, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { AnsiConsole.MarkupLine("[yellow]Could not remove temporary firmware files:[/] " + Markup.Escape(workspace)); }
    }

    internal static async Task<ArduinoTools> PrepareToolsAsync(string workspace, CancellationToken token, bool requireCore = true)
    {
        var tools = new ArduinoTools(await EnsureCliAsync(workspace, token));
        if (!requireCore) return tools;
        bool installed;
        try { installed = await tools.HasCoreAsync(token); }
        catch (Exception ex) when (ex is IOException or JsonException)
        { throw new IOException("ESP32 core discovery failed. Check Arduino CLI configuration and directory permissions. " + ex.Message, ex); }
        if (installed) return tools;
        string directory = await tools.DataDirectoryAsync(token);
        AnsiConsole.MarkupLine("Arduino CLI is available. The separate ESP32 core/compiler and Espressif flashing tools are missing. Persistent installation uses: " + Markup.Escape(directory));
        string choice = Choose("ESP32 tools (large download)", "Install once for reuse", "Download temporarily", "Cancel");
        if (choice == "Cancel") throw new OperationCanceledException(token);
        if (choice == "Download temporarily") tools.UseTemporaryConfig(workspace);
        await Busy("Installing ESP32 tools…", async () => { await tools.InstallCoreAsync(token); return true; });
        return tools;
    }

    internal static Task<int> ManageAsync(string action, string? listen, CancellationToken token) =>
        WithWorkspaceAsync(async workspace =>
        {
            var tools = await PrepareToolsAsync(workspace, token, requireCore: action == "clear");
            string port = await SelectPortAsync(tools, token);
            if (action == "update") return await FirmwareUpdate.RunAsync(tools, port, listen, token);
            AnsiConsole.MarkupLine("Selected device: " + Markup.Escape(port));
            AnsiConsole.MarkupLine("Full erase removes application firmware, flash bootloader/partitions, and saved settings including Wi-Fi credentials. The immutable ROM download bootloader remains. Factory firmware is not restored; eFuses/security settings are unchanged.");
            try
            {
                bool erased = await EraseConfirmedAsync(() => AnsiConsole.Confirm("Erase all writable flash on this selected ESP32-C3?", false),
                    () => Busy("Erasing flash…", async () => { await tools.EraseAsync(port, workspace, token); return true; }));
                if (!erased) return 130;
            }
            catch (IOException ex) { throw new IOException(ex.Message + " Hold BOOT, tap RESET, release BOOT; check USB cable, serial permissions and close other serial applications.", ex); }
            AnsiConsole.MarkupLine("[green]Flash erased.[/] Run tempfold firmware setup to install TFirm.");
            return 0;
        });

    internal static async Task<bool> EraseConfirmedAsync(Func<bool> confirm, Func<Task<bool>> erase)
    {
        if (!confirm()) return false;
        return await erase();
    }

    internal static string[] PortChoices(BoardPort[] ports) =>
        ports.Select(p => $"{p.Address} — {p.Label}").Concat(["Refresh", "Cancel"]).ToArray();

    private static async Task<string> EnsureCliAsync(string workspace, CancellationToken token)
    {
        while (true)
        {
            try { await ToolProcess.RunAsync("arduino-cli", ["version"], token); return "arduino-cli"; }
            catch (IOException) { }
            AnsiConsole.MarkupLine("Arduino CLI is needed for flashing. Install it from https://docs.arduino.cc/arduino-cli/installation/ or use a temporary download.");
            string choice = Choose("Arduino CLI", "Download it temporarily", "I installed it — check PATH again", "Cancel");
            if (choice == "Cancel") throw new OperationCanceledException(token);
            if (choice == "Download it temporarily") return await Busy("Downloading and verifying Arduino CLI…", () => TemporaryArduinoCli.DownloadAsync(workspace, token));
        }
    }

    internal static async Task<string> SelectPortAsync(ArduinoTools tools, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            BoardPort[] ports = await Busy("Finding connected devices…", () => tools.BoardsAsync(token));
            if (ports.Length == 0) AnsiConsole.MarkupLine("[yellow]No serial devices found. Connect the ESP32-C3 with a data-capable USB cable.[/]");
            string[] choices = PortChoices(ports);
            string chosen = Choose("Select the ESP32-C3 (unidentified serial devices may be other hardware)", choices);
            if (chosen == "Cancel") throw new OperationCanceledException(token);
            if (chosen == "Refresh") continue;
            return ports[Array.IndexOf(choices, chosen)].Address;
        }
    }
    internal static async Task<string> ReconnectPortAsync(ArduinoTools tools, string old, CancellationToken token)
    {
        if ((await tools.BoardsAsync(token)).Any(p => p.Address == old)) return old;
        AnsiConsole.MarkupLine("[yellow]The device's serial port changed after restarting. Select the ESP32-C3 again.[/]");
        return await SelectPortAsync(tools, token);
    }
    internal static string Choose(string title, params string[] choices) => AnsiConsole.Prompt(new SelectionPrompt<string>().Title(title)
        .UseConverter(Markup.Escape).AddChoices(choices));
    internal static string AskText(string title, int limit, string? initial = null)
    {
        var prompt = new TextPrompt<string>(title).Validate(s => !string.IsNullOrWhiteSpace(s) && FirmwareValidation.ValidText(s, limit)
            ? ValidationResult.Success() : ValidationResult.Error($"Enter nonblank text up to {limit} UTF-8 bytes, without control characters."));
        if (initial is not null) prompt.DefaultValue(initial);
        return AnsiConsole.Prompt(prompt);
    }
    internal static string AskEndpoint(string? listen)
    {
        int port = 5147;
        if (!string.IsNullOrEmpty(listen))
        {
            string first = listen.Split(';')[0].Replace("*", "0.0.0.0").Replace("+", "0.0.0.0");
            if (!Uri.TryCreate(first, UriKind.Absolute, out var uri) || uri.Scheme != "http")
                throw new InvalidDataException("TFirm supports HTTP only. Supply --api-listen-url with the actual HTTP listener used by TempFold.");
            port = uri.Port;
        }
        BoardPort[] connections = HostNetwork.LanConnections();
        var prompt = new TextPrompt<string>("TempFold server address (confirm the IP and port are reachable from this Wi-Fi):")
            .Validate(s => FirmwareValidation.ValidEndpoint(s) ? ValidationResult.Success() : ValidationResult.Error("Use http://LAN-IP:port without a path or trailing slash."));
        if (connections.Length > 0)
        {
            string[] choices = connections.Select(c => $"{c.Address} — {c.Label}").ToArray();
            string address = connections.Length == 1 ? connections[0].Address : connections[Array.IndexOf(choices,
                Choose("Select this computer's LAN adapter (check VPN/reachability)", choices))].Address;
            prompt.DefaultValue($"http://{address}:{port}");
        }
        AnsiConsole.MarkupLine("The server must listen on a LAN address (e.g. http://0.0.0.0:5147) and allow inbound traffic. 5147 is the setup default when no listener is configured.");
        return AnsiConsole.Prompt(prompt);
    }
    internal static Task<T> Busy<T>(string text, Func<Task<T>> action) => AnsiConsole.Status().Spinner(Spinner.Known.Dots).StartAsync(text, _ => action());
    internal static void ValidateDevice(JsonElement status, string? expectedVersion)
    {
        if (status.GetProperty("chip").GetString() != "ESP32-C3" || status.GetProperty("protocolVersion").GetInt32() != 1)
            throw new InvalidDataException("The board is not running compatible ESP32-C3 TFirm firmware.");
        if (expectedVersion is not null && status.GetProperty("firmwareVersion").GetString() != expectedVersion)
            throw new InvalidDataException("The board's firmware version differs from the selected release.");
    }
    internal static void ValidateSettings(JsonElement status, DeviceSettings settings)
    {
        if (status.GetProperty("boardName").GetString() != settings.BoardName || status.GetProperty("wifiSsid").GetString() != settings.WifiSsid ||
            status.GetProperty("apiEndpoint").GetString() != settings.ApiEndpoint || status.GetProperty("restartRequired").GetBoolean() ||
            status.GetProperty("wifiPasswordSet").GetBoolean() != (settings.WifiPassword.Length > 0))
            throw new InvalidDataException("The board's saved settings did not survive restart.");
    }
}
