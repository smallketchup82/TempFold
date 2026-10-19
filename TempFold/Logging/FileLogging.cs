// Copyright (c) 2026 smallketchup82 <ketchup@smkt.ca>. Licensed under the MIT License.
// See the LICENSE file in the repository root for full license text.

using System.Text.Json;
using Serilog;
using Serilog.Events;

namespace TempFold.Logging;

internal static class FileLogging
{
    internal static bool TryLevel(string value, out LogEventLevel level) =>
        Enum.TryParse(value, true, out level) && Enum.IsDefined(level);

    internal static string DefaultDirectory()
    {
        if (OperatingSystem.IsLinux())
        {
            string? state = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
            if (string.IsNullOrEmpty(state) || !Path.IsPathFullyQualified(state))
                state = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state");
            return Path.Combine(state, "tempfold", "logs");
        }
        if (OperatingSystem.IsMacOS())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Logs", "TempFold");
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TempFold", "logs");
    }

    internal static void Configure(LoggerConfiguration configuration, string? path,
        string? directory = null, string level = "verbose", long sizeLimit = 20 * 1024 * 1024, int retainedFiles = 31)
    {
        if (path is null && directory is null) return;
        if (path is not null && directory is not null) throw new ArgumentException("Choose a log file or directory, not both.");
        if (!TryLevel(level, out var severity)) throw new ArgumentException("Invalid log level.");
        string fullPath = Path.GetFullPath(path ?? Path.Combine(directory == "default" ? DefaultDirectory() : directory!, "tempfold-.jsonl"));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        if (directory is null && File.Exists(fullPath))
            foreach (string line in File.ReadLines(fullPath))
            {
                try
                {
                    using var json = JsonDocument.Parse(line);
                    if (json.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("Expected an object.");
                }
                catch (JsonException ex)
                {
                    throw new InvalidDataException("Existing log file is not JSON Lines; choose a new path or archive the old text log.", ex);
                }
            }
        // Probe without leaving an empty non-rolling file in directory mode.
        string probe = directory is null ? fullPath : Path.Combine(Path.GetDirectoryName(fullPath)!, $".probe-{Guid.NewGuid():N}");
        using (new FileStream(probe, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { }
        if (directory is not null) File.Delete(probe);
        configuration.WriteTo.File(new JsonLineFormatter(), fullPath, restrictedToMinimumLevel: severity,
            fileSizeLimitBytes: sizeLimit, rollOnFileSizeLimit: true,
            rollingInterval: directory is null ? RollingInterval.Infinite : RollingInterval.Day,
            retainedFileCountLimit: retainedFiles);
    }
}
