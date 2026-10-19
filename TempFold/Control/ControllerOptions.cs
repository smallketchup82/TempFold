// Copyright (c) 2026 smallketchup82 <ketchup@smkt.ca>. Licensed under the MIT License.
// See the LICENSE file in the repository root for full license text.

namespace TempFold.Control;

internal sealed record ControllerOptions(
    double Target,
    double Range,
    string PortName,
    int BaudRate,
    Uri FahEndpoint)
{
    public string? ApiListenUrl { get; init; }
    public bool PauseOnGameMode { get; init; } = true;
    public bool QuitOnFahDisconnect { get; init; }
    public bool PauseFahOnExit { get; init; } = true;
    public double SensorTimeoutSeconds { get; init; }
    public double FahConnectionTimeoutSeconds { get; init; } = Configuration.AppConfig.DefaultFahConnectionTimeoutSeconds;
    public string[] AllowedTransmissionSources { get; init; } = Configuration.AppConfig.DefaultTransmissionSources;
    public string? LogFile { get; init; }
    public string? LogDirectory { get; init; }
    public string Verbosity { get; init; } = "information";
    public string LogLevel { get; init; } = "verbose";
    public long LogMaxSizeBytes { get; init; } = 20 * 1024 * 1024;
    public int LogRetainedFiles { get; init; } = 31;
    public Configuration.Schedule[]? Schedule { get; init; }
    public TimeZoneInfo? TimeZone { get; init; }
    public bool NoSchedule { get; init; }
    public bool StartPaused { get; init; }
    public bool AllowsApi => AllowedTransmissionSources.Contains("api", StringComparer.Ordinal);
    public bool AllowsSerial => AllowedTransmissionSources.Contains("serial", StringComparer.Ordinal);
    public Logging.StatusOutputOptions Status { get; init; } = new();
    public double StartThreshold => Target - Range;
    public double StopThreshold => Target + Range;
}
