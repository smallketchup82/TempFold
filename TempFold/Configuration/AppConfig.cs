// Copyright (c) 2026 smallketchup82 <ketchup@smkt.ca>. Licensed under the MIT License.
// See the LICENSE file in the repository root for full license text.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace TempFold.Configuration;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record AppConfig(
    string? ApiListenUrl = null,
    string? FahEndpoint = null,
    string? SerialPort = null,
    int? SerialBaud = null,
    bool? PauseOnGameMode = null,
    bool? QuitOnFahDisconnect = null,
    bool? PauseFahOnExit = null,
    double? SensorTimeoutSeconds = null,
    double? FahConnectionTimeoutSeconds = null,
    string[]? AllowedTransmissionSources = null,
    string? LogFile = null,
    string? TimeZone = null,
    Schedule[]? Schedule = null,
    string? LogDirectory = null,
    string? Verbosity = null,
    string? LogLevel = null,
    long? LogMaxSizeBytes = null,
    int? LogRetainedFiles = null)
{
    internal const string DefaultFahEndpoint = "ws://127.0.0.1:7396/api/websocket";
    internal const string DefaultSerialPort = "/dev/ttyACM0";
    internal const int DefaultSerialBaud = 115200;
    internal const double DefaultFahConnectionTimeoutSeconds = 900;
    internal static readonly string[] DefaultTransmissionSources = ["api", "serial"];
    internal static AppConfig Template => new("http://0.0.0.0:5147", DefaultFahEndpoint, DefaultSerialPort,
        DefaultSerialBaud, true, false, true, 0, DefaultFahConnectionTimeoutSeconds, DefaultTransmissionSources);

    internal void Validate()
    {
        if (ApiListenUrl is not null && !IsListenUrl(ApiListenUrl))
            throw new InvalidDataException("apiListenUrl must be an http:// or https:// listening URL with a valid host and port, without credentials, path, query, or fragment.");
        if (FahEndpoint is not null && !IsFahEndpoint(FahEndpoint))
            throw new InvalidDataException("fahEndpoint must be an absolute ws:// or wss:// URL without credentials or a fragment.");
        if (SerialPort is not null && (string.IsNullOrWhiteSpace(SerialPort) || SerialPort.Any(char.IsControl)))
            throw new InvalidDataException("serialPort must be a nonempty device name without control characters.");
        if (SerialBaud is <= 0)
            throw new InvalidDataException("serialBaud must be greater than 0.");
        if (SensorTimeoutSeconds is { } sensorTimeout && !IsTimeout(sensorTimeout))
            throw new InvalidDataException("sensorTimeoutSeconds must be finite, nonnegative, and less than TimeSpan.MaxValue in seconds (0 disables the timeout).");
        if (FahConnectionTimeoutSeconds is { } fahTimeout && !IsTimeout(fahTimeout))
            throw new InvalidDataException("fahConnectionTimeoutSeconds must be finite, nonnegative, and less than TimeSpan.MaxValue in seconds (0 disables the timeout).");
        if (AllowedTransmissionSources is { } sources && !AreTransmissionSources(sources))
            throw new InvalidDataException("allowedTransmissionSources must be a nonempty array containing only \"api\" and/or \"serial\".");
        if (LogFile is not null && !IsLogFile(LogFile))
            throw new InvalidDataException("logFile must be a nonempty file path without control characters.");
        if (LogDirectory is not null && !IsLogFile(LogDirectory))
            throw new InvalidDataException("logDirectory must be a nonempty path or default.");
        if (LogDirectory is not null && LogFile is not null)
            throw new InvalidDataException("Choose logFile or logDirectory, not both.");
        if ((Verbosity is not null && !Logging.FileLogging.TryLevel(Verbosity, out _))
            || (LogLevel is not null && !Logging.FileLogging.TryLevel(LogLevel, out _)))
            throw new InvalidDataException("Invalid logging level.");
        if (LogMaxSizeBytes is <= 0 || LogRetainedFiles is <= 0)
            throw new InvalidDataException("Logging size and retention must be positive.");
        if (TimeZone is not null)
            ResolveTimeZone(TimeZone);
        if (Schedule is { } schedules)
            foreach (Schedule schedule in schedules)
            {
                if (schedule is null)
                    throw new InvalidDataException("schedule cannot contain null entries.");
                schedule.Validate();
            }
    }

    internal static TimeZoneInfo ResolveTimeZone(string id)
    {
        if (!TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out _))
            throw new InvalidDataException("timeZone must be an IANA timezone such as America/Toronto.");
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new InvalidDataException($"Timezone {id} is unavailable: {ex.Message}", ex);
        }
    }

    internal static bool IsTimeout(double seconds) =>
        double.IsFinite(seconds) && seconds >= 0 && seconds < TimeSpan.MaxValue.TotalSeconds;

    internal static bool AreTransmissionSources(string[] sources) =>
        sources.Length > 0 && sources.All(source => source is "api" or "serial");

    internal static bool IsLogFile(string value) =>
        !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl);

    internal static bool IsFahEndpoint(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out Uri? endpoint)
        && endpoint.Scheme is "ws" or "wss" && !string.IsNullOrEmpty(endpoint.Host)
        && endpoint.Port is > 0 and <= 65535
        && string.IsNullOrEmpty(endpoint.UserInfo) && string.IsNullOrEmpty(endpoint.Fragment)
        && !value.Any(char.IsWhiteSpace);

    internal static bool IsListenUrl(string value)
    {
        // Kestrel also accepts wildcard bind hosts.
        string normalized = value.Replace("://*:", "://0.0.0.0:", StringComparison.Ordinal)
            .Replace("://+:", "://0.0.0.0:", StringComparison.Ordinal);
        return Uri.TryCreate(normalized, UriKind.Absolute, out Uri? endpoint)
            && endpoint.Scheme is "http" or "https" && !string.IsNullOrEmpty(endpoint.Host)
            && endpoint.Port is > 0 and <= 65535 && endpoint.AbsolutePath == "/"
            && string.IsNullOrEmpty(endpoint.UserInfo) && string.IsNullOrEmpty(endpoint.Query)
            && string.IsNullOrEmpty(endpoint.Fragment) && !value.Any(char.IsWhiteSpace);
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AppConfig))]
internal partial class ConfigJsonContext : JsonSerializerContext;
