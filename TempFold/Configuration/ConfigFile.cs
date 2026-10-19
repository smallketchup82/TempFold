// Copyright (c) 2026 smallketchup82 <ketchup@smkt.ca>. Licensed under the MIT License.
// See the LICENSE file in the repository root for full license text.

using System.Text.Json;

namespace TempFold.Configuration;

internal static class ConfigFile
{
    internal static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "config.json");

    internal static string UserPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "tempfold", "config.json");

    internal static async Task<AppConfig> ReadAsync(string path, CancellationToken token)
    {
        await using FileStream file = File.OpenRead(path);
        using JsonDocument document = await JsonDocument.ParseAsync(file, cancellationToken: token);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("The configuration must be a JSON object.");
        ValidateMembers(document.RootElement, "configuration");
        AppConfig config = document.RootElement.Deserialize(ConfigJsonContext.Default.AppConfig)!;
        config.Validate();
        return config;
    }

    private static void ValidateMembers(JsonElement element, string path)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
                ValidateMembers(item, path + "[]");
        }
        else if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new InvalidDataException($"Duplicate configuration setting: {path}.{property.Name}.");
                ValidateMembers(property.Value, path + "." + property.Name);
            }
        }
        else if (element.ValueKind == JsonValueKind.Null)
            throw new InvalidDataException($"Configuration setting {path} cannot be null; omit optional settings to use the default.");
    }

    internal static async Task<(AppConfig Config, string? Path)> LoadOptionalAsync(string? requestedPath,
        CancellationToken token, string? defaultPath = null, string? userPath = null)
    {
        string[] paths = requestedPath is not null ? [requestedPath]
            : defaultPath is not null && userPath is null ? [defaultPath]
            : [defaultPath ?? DefaultPath, userPath ?? UserPath];
        foreach (string candidate in paths)
        {
            string path = System.IO.Path.GetFullPath(candidate);
            // Only missing files allow fallback; invalid or unreadable files must fail.
            try { return (await ReadAsync(path, token), path); }
            catch (FileNotFoundException) when (requestedPath is null) { }
            catch (DirectoryNotFoundException) when (requestedPath is null) { }
        }
        return (new(), null);
    }

    internal static async Task CreateAsync(string path, CancellationToken token)
    {
        AppConfig.Template.Validate();
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(file, AppConfig.Template, ConfigJsonContext.Default.AppConfig, token);
        await file.WriteAsync("\n"u8.ToArray(), token);
    }
}
