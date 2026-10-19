// Copyright (c) 2026 smallketchup82 <ketchup@smkt.ca>. Licensed under the MIT License.
// See the LICENSE file in the repository root for full license text.

using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace TempFold.Firmware;

internal sealed record FirmwareImage(string Path, string? Version)
{
    internal const int MaximumBytes = 32 * 1024 * 1024;
    internal static async Task<FirmwareImage> LoadAsync(string source, string workspace, CancellationToken token)
    {
        if (new FileInfo(source).Length > MaximumBytes) throw new InvalidDataException("Firmware exceeds the 32 MB limit.");
        string destination = System.IO.Path.Combine(workspace, "firmware.merged.bin");
        string? version = null;
        if (source.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
        {
            await using var input = File.OpenRead(source);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var archive = new TarReader(gzip);
            byte[]? image = null;
            byte[]? manifest = null;
            long total = 0;
            int count = 0;
            TarEntry? entry;
            while ((entry = await archive.GetNextEntryAsync(cancellationToken: token)) is not null)
            {
                if (++count > 32 || entry.Length > MaximumBytes || (total += entry.Length) > MaximumBytes)
                    throw new InvalidDataException("Firmware archive exceeds limits.");
                if (entry.Name is not ("TFirm.ino.merged.bin" or "manifest.json")) continue;
                if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || entry.DataStream is null)
                    throw new InvalidDataException("Firmware archive contains an invalid entry.");
                using var data = new MemoryStream();
                await entry.DataStream.CopyToAsync(data, token);
                if (entry.Name == "manifest.json")
                {
                    if (manifest is not null || data.Length > 65536) throw new InvalidDataException("Invalid firmware manifest.");
                    manifest = data.ToArray();
                }
                else
                {
                    if (image is not null) throw new InvalidDataException("Duplicate firmware image.");
                    image = data.ToArray();
                }
            }
            if (image is null || manifest is null) throw new InvalidDataException("Select a TFirm release archive containing its merged image and manifest.");
            using JsonDocument doc = JsonDocument.Parse(manifest);
            JsonElement root = doc.RootElement;
            if (root.GetProperty("chip").GetString() != "esp32c3" || root.GetProperty("protocolVersion").GetInt32() != 1 ||
                root.GetProperty("factoryImage").GetProperty("file").GetString() != "TFirm.ino.merged.bin" ||
                root.GetProperty("factoryImage").GetProperty("offset").GetString() != "0x0")
                throw new InvalidDataException("This installer supports ESP32-C3 TFirm protocol v1 factory images only.");
            JsonElement[] matches = root.GetProperty("files").EnumerateArray()
                .Where(f => f.GetProperty("file").GetString() == "TFirm.ino.merged.bin").ToArray();
            if (matches.Length != 1 || matches[0].GetProperty("bytes").GetInt64() != image.Length ||
                !string.Equals(matches[0].GetProperty("sha256").GetString(), Convert.ToHexString(SHA256.HashData(image)), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Firmware checksum or size does not match the manifest.");
            version = root.GetProperty("firmwareVersion").GetString();
            await File.WriteAllBytesAsync(destination, image, token);
        }
        else
        {
            if (!source.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Select a TFirm .tar.gz archive or merged .bin image.");
            File.Copy(source, destination, overwrite: true);
        }
        await using var binary = File.OpenRead(destination);
        ValidateMerged(binary);
        return new(destination, version);
    }

    internal static void ValidateMerged(Stream stream)
    {
        // Both images must target C3; application-only files cannot initialize a blank board.
        Span<byte> header = stackalloc byte[24];
        foreach (long offset in new long[] { 0, 0x10000 })
        {
            stream.Position = offset;
            if (stream.Read(header) != header.Length || header[0] != 0xE9 || header[12] != 5 || header[13] != 0)
                throw new InvalidDataException("Expected a merged ESP32-C3 image with bootloader at 0 and application at 0x10000; application-only binaries are unsupported.");
        }
        stream.Position = 0x8000;
        if (stream.ReadByte() != 0xAA || stream.ReadByte() != 0x50)
            throw new InvalidDataException("The merged image has no supported partition table at 0x8000.");
    }

    internal static bool IsRepository(string value) => value.Split('/') is [var owner, var repo] &&
        owner.Length > 0 && repo.Length > 0 && owner.Concat(repo).All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    internal static async Task<string?> DownloadLatestAsync(string repository, string workspace, CancellationToken token)
    {
        if (!IsRepository(repository)) throw new InvalidDataException("Use a GitHub repository in owner/repository format.");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("TempFold-firmware-installer/1.0");
        using var response = await http.GetAsync($"https://api.github.com/repos/{repository}/releases?per_page=100", token);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        foreach (JsonElement release in document.RootElement.EnumerateArray()
            .Where(r => !r.GetProperty("draft").GetBoolean() && !r.GetProperty("prerelease").GetBoolean())
            .OrderByDescending(r => r.GetProperty("published_at").GetDateTimeOffset()))
        {
            foreach (JsonElement asset in release.GetProperty("assets").EnumerateArray())
            {
                string name = asset.GetProperty("name").GetString() ?? "";
                if (!name.StartsWith("TFirm-", StringComparison.Ordinal) || !name.EndsWith("-esp32c3.tar.gz", StringComparison.Ordinal)) continue;
                if (asset.GetProperty("size").GetInt64() > MaximumBytes) throw new InvalidDataException("Release asset exceeds the size limit.");
                string path = System.IO.Path.Combine(workspace, "download.tar.gz");
                using var download = await http.GetAsync(asset.GetProperty("browser_download_url").GetString(), HttpCompletionOption.ResponseHeadersRead, token);
                download.EnsureSuccessStatusCode();
                await using var source = await download.Content.ReadAsStreamAsync(token);
                await using var file = File.Create(path);
                byte[] buffer = new byte[81920];
                int read; long size = 0;
                while ((read = await source.ReadAsync(buffer, token)) != 0)
                {
                    if ((size += read) > MaximumBytes) throw new InvalidDataException("Release download exceeds the size limit.");
                    await file.WriteAsync(buffer.AsMemory(0, read), token);
                }
                return path;
            }
        }
        return null;
    }
}
