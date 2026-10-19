// Copyright (c) 2026 smallketchup82 <ketchup@smkt.ca>. Licensed under the MIT License.
// See the LICENSE file in the repository root for full license text.

using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace TempFold.Firmware;

internal static class TemporaryArduinoCli
{
    internal static async Task<string> DownloadAsync(string workspace, CancellationToken token)
    {
        string os = OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : OperatingSystem.IsLinux() ? "Linux" : throw new PlatformNotSupportedException("Download Arduino CLI manually for this operating system.");
        string architecture = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "64bit", Architecture.Arm64 => "ARM64", Architecture.Arm => "ARMv7",
            _ => throw new PlatformNotSupportedException("Download Arduino CLI manually for this architecture.")
        };
        string suffix = $"_{os}_{architecture}." + (OperatingSystem.IsWindows() ? "zip" : "tar.gz");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("TempFold-firmware-installer/1.0");
        using JsonDocument release = JsonDocument.Parse(await http.GetStringAsync("https://api.github.com/repos/arduino/arduino-cli/releases/latest", token));
        var assets = release.RootElement.GetProperty("assets").EnumerateArray().ToArray();
        JsonElement asset = assets.FirstOrDefault(a => a.GetProperty("name").GetString()!.EndsWith(suffix, StringComparison.Ordinal));
        JsonElement checksums = assets.FirstOrDefault(a => a.GetProperty("name").GetString()!.EndsWith("checksums.txt", StringComparison.Ordinal));
        if (asset.ValueKind == JsonValueKind.Undefined || checksums.ValueKind == JsonValueKind.Undefined)
            throw new IOException("No official Arduino CLI archive/checksums found for this platform. Install Arduino CLI manually.");
        if (asset.GetProperty("size").GetInt64() > 64 * 1024 * 1024) throw new IOException("Arduino CLI archive exceeds the download limit.");
        byte[] bytes = await http.GetByteArrayAsync(asset.GetProperty("browser_download_url").GetString(), token);
        string hashes = await http.GetStringAsync(checksums.GetProperty("browser_download_url").GetString(), token);
        string name = asset.GetProperty("name").GetString()!;
        string[]? line = hashes.Split('\n').Select(l => l.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .FirstOrDefault(parts => parts.Length == 2 && parts[1].TrimStart('*') == name);
        if (bytes.Length > 64 * 1024 * 1024 || line is null || !string.Equals(line[0], Convert.ToHexString(SHA256.HashData(bytes)), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Arduino CLI archive checksum verification failed.");
        string executable = OperatingSystem.IsWindows() ? "arduino-cli.exe" : "arduino-cli";
        string destination = Path.Combine(workspace, executable);
        using var memory = new MemoryStream(bytes);
        await using var output = File.Create(destination);
        if (OperatingSystem.IsWindows())
        {
            using var zip = new ZipArchive(memory);
            var entry = zip.GetEntry(executable) ?? throw new IOException("Arduino CLI executable is missing from its archive.");
            if (entry.Length > 128 * 1024 * 1024) throw new IOException("Arduino CLI executable is oversized.");
            await using var input = entry.Open(); await input.CopyToAsync(output, token);
        }
        else
        {
            using var gzip = new GZipStream(memory, CompressionMode.Decompress);
            using var tar = new TarReader(gzip);
            TarEntry? entry; bool found = false;
            while ((entry = await tar.GetNextEntryAsync(cancellationToken: token)) is not null)
                if (entry.Name == executable && entry.DataStream is not null && entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile)
                {
                    if (entry.Length > 128 * 1024 * 1024) throw new IOException("Arduino CLI executable is oversized.");
                    await entry.DataStream.CopyToAsync(output, token); found = true; break;
                }
            if (!found) throw new IOException("Arduino CLI executable is missing from its archive.");
        }
        await output.DisposeAsync();
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(destination, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return destination;
    }
}
