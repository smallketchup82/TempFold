// Copyright (c) 2026 smallketchup82 <ketchup@smkt.ca>. Licensed under the MIT License.
// See the LICENSE file in the repository root for full license text.

using System.IO.Ports;
using System.Text.Json;

namespace TempFold.Firmware;

internal sealed record BoardPort(string Address, string Label);
internal sealed class ArduinoTools(string cli)
{
    internal const string CoreVersion = "3.3.12";
    internal const string Index = "https://espressif.github.io/arduino-esp32/package_esp32_index.json";
    internal const string Board = "esp32:esp32:esp32c3:CDCOnBoot=cdc,FlashSize=4M,PartitionScheme=default";

    private string? configFile;
    private Task<string> RunAsync(IEnumerable<string> arguments, CancellationToken token, TimeSpan? timeout = null) =>
        ToolProcess.RunAsync(cli, configFile is null ? arguments : arguments.Concat(["--config-file", configFile]), token, timeout);

    internal void UseTemporaryConfig(string workspace, string? existingCoreData = null)
    {
        configFile = Path.Combine(workspace, "arduino-cli.yaml");
        using var stream = File.Create(configFile);
        using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartObject(); writer.WriteStartObject("directories");
        foreach (string kind in new[] { "data", "downloads", "user" })
        {
            string path = kind == "data" && existingCoreData is not null ? existingCoreData : Path.Combine(workspace, "arduino-" + kind);
            Directory.CreateDirectory(path);
            writer.WriteString(kind, path);
        }
        writer.WriteEndObject();
        writer.WriteStartObject("build_cache"); writer.WriteString("path", Path.Combine(workspace, "arduino-cache"));
        writer.WriteEndObject(); writer.WriteEndObject();
    }

    internal async Task<BoardPort[]> BoardsAsync(CancellationToken token)
    {
        string json = await RunAsync( ["board", "list", "--json"], token);
        using JsonDocument doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("warnings", out var warnings))
            foreach (var warning in warnings.EnumerateArray())
                Spectre.Console.AnsiConsole.MarkupLine("[yellow]Device discovery: " + Spectre.Console.Markup.Escape(warning.GetString() ?? "Unknown discovery warning") + "[/]");
        var ports = new Dictionary<string, BoardPort>(StringComparer.Ordinal);
        if (doc.RootElement.TryGetProperty("detected_ports", out JsonElement detected))
            foreach (JsonElement item in detected.EnumerateArray())
            {
                JsonElement port = item.GetProperty("port");
                if (port.GetProperty("protocol").GetString() != "serial") continue;
                string address = port.GetProperty("address").GetString()!;
                string label = port.TryGetProperty("label", out var l) ? l.GetString() ?? address : address;
                if (item.TryGetProperty("matching_boards", out var boards) && boards.GetArrayLength() > 0)
                    label += " — " + boards[0].GetProperty("name").GetString();
                ports[address] = new(address, label);
            }
        // Generic C3 clones often have no board identity. Never claim every serial port is a C3.
        foreach (string address in SerialPort.GetPortNames()) ports.TryAdd(address, new(address, "Unidentified serial device"));
        return ports.Values.OrderBy(p => p.Address, StringComparer.Ordinal).ToArray();
    }

    internal async Task<bool> HasCoreAsync(CancellationToken token)
    {
        using JsonDocument doc = JsonDocument.Parse(await RunAsync( ["core", "list", "--json"], token));
        return doc.RootElement.TryGetProperty("platforms", out var platforms) && platforms.EnumerateArray()
            .Any(p => p.GetProperty("id").GetString() == "esp32:esp32" && p.TryGetProperty("installed_version", out var version) && version.GetString() == CoreVersion);
    }

    internal async Task<string> DataDirectoryAsync(CancellationToken token)
    {
        using var doc = JsonDocument.Parse(await RunAsync(["config", "get", "directories.data", "--json"], token));
        string? path = doc.RootElement.ValueKind == JsonValueKind.String ? doc.RootElement.GetString() : null;
        if (string.IsNullOrWhiteSpace(path)) throw new IOException("Arduino CLI did not report its effective data directory.");
        return path;
    }

    internal async Task InstallCoreAsync(CancellationToken token)
    {
        await RunAsync( ["core", "update-index", "--additional-urls", Index], token, TimeSpan.FromMinutes(10));
        await RunAsync( ["core", "install", $"esp32:esp32@{CoreVersion}", "--additional-urls", Index], token, TimeSpan.FromMinutes(20));
    }

    internal async Task<string> CompileLatestAsync(string repository, string workspace, CancellationToken token)
    {
        if (!FirmwareImage.IsRepository(repository)) throw new InvalidDataException("Use owner/repository for the source repository.");
        // Keep the selected reusable core while isolating sketch libraries so
        // pinning build dependencies never replaces the user's installed libraries.
        string coreData = await DataDirectoryAsync(token);
        UseTemporaryConfig(workspace, coreData);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("TempFold-firmware-installer/1.0");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github.raw+json");
        string source = await http.GetStringAsync($"https://api.github.com/repos/{repository}/contents/firmware/TFirm/TFirm.ino", token);
        if (source.Length > 256 * 1024) throw new InvalidDataException("Firmware source exceeds limits.");
        string sketch = Path.Combine(workspace, "TFirm");
        Directory.CreateDirectory(sketch);
        await File.WriteAllTextAsync(Path.Combine(sketch, "TFirm.ino"), source, token);
        await RunAsync(["lib", "update-index"], token, TimeSpan.FromMinutes(5));
        await RunAsync(["lib", "install", "ArduinoJson@7.4.3", "Adafruit SHT31 Library@2.2.2", "Adafruit BusIO@1.17.4"], token, TimeSpan.FromMinutes(10));
        string output = Path.Combine(workspace, "compiled");
        await RunAsync(["compile", "--fqbn", Board, "--warnings", "all", "--output-dir", output, "--build-path", Path.Combine(workspace, "compile-work"), sketch], token, TimeSpan.FromMinutes(15));
        return Path.Combine(output, "TFirm.ino.merged.bin");
    }

    // No bootloader protocol here: discover and invoke Espressif's flasher bundled
    // by Arduino CLI. --show-properties resolves paths without compiling firmware.
    internal Task FlashAsync(FirmwareImage image, string port, string workspace, CancellationToken token) =>
        RunFlasherAsync(port, workspace, ["write-flash", "0x0", image.Path], token);

    internal Task EraseAsync(string port, string workspace, CancellationToken token) =>
        RunFlasherAsync(port, workspace, ["erase-flash"], token);

    private async Task RunFlasherAsync(string port, string workspace, string[] action, CancellationToken token)
    {
        string sketch = Path.Combine(workspace, "Probe");
        Directory.CreateDirectory(sketch);
        await File.WriteAllTextAsync(Path.Combine(sketch, "Probe.ino"), "void setup() {}\nvoid loop() {}\n", token);
        string output = await RunAsync( ["compile", "--fqbn", Board, "--show-properties=expanded", "--build-path", Path.Combine(workspace, "probe-work"), sketch], token);
        Dictionary<string, string> properties = output.Split('\n').Where(l => l.Contains('='))
            .Select(l => l.TrimEnd('\r').Split('=', 2)).GroupBy(p => p[0]).ToDictionary(g => g.Key, g => g.Last()[1]);
        if (!properties.TryGetValue("tools.esptool_py.path", out string? directory) ||
            !properties.TryGetValue("tools.esptool_py.cmd", out string? command))
            throw new IOException("Arduino CLI did not expose its ESP32 flashing tool. Install ESP32 core 3.3.12.");
        string tool = Path.Combine(directory, command);
        await ToolProcess.RunAsync(tool, ["--chip", "esp32c3", "--port", port, "--baud", "460800", "--before", "default-reset",
            "--after", "hard-reset", .. action], token, TimeSpan.FromMinutes(3));
    }
}
