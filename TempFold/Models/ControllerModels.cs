// Copyright (c) 2026 smallketchup82 <ketchup@smkt.ca>. Licensed under the MIT License.
// See the LICENSE file in the repository root for full license text.

namespace TempFold.Models;

internal sealed record TemperatureReading(DateTimeOffset Timestamp, double Temperature);

internal sealed record GameModeStatus(bool? IsActive, string? Error);

internal sealed record FoldingStatus(bool IsConfiguredToFold, bool IsWorking);

internal enum PauseReason { Manual, GameMode }

internal sealed record SensorRequest(double? Temperature);

internal sealed record SensorError(string Error);

internal sealed record QueuedSensorReading(double Temperature, string Source = "unknown");

internal sealed record TemperatureSettings(
    double Target,
    double Range,
    bool TargetOverridden = false,
    bool RangeOverridden = false,
    string? ScheduleName = null,
    bool SchedulingEnabled = false);

internal sealed record ControllerSnapshot(
    double? Temperature,
    DateTimeOffset? LastSensorUpdate,
    IReadOnlyList<TemperatureReading> History,
    bool? GameModeActive,
    FoldingStatus? FoldingStatus)
{
    public TemperatureSettings Settings { get; init; } = new(0, 0);
    public PauseReason? PauseReason { get; init; }
    public bool Paused => PauseReason is not null;
}

internal sealed record StatusReport(
    DateTimeOffset Timestamp,
    double? Temperature,
    DateTimeOffset? LastSensorUpdate,
    double? Trend5Minutes,
    double? Trend15Minutes,
    double? Trend60Minutes,
    bool? GameModeActive,
    bool FahConnected,
    bool? FoldingActive,
    double Target,
    double Range,
    bool TargetOverridden = false,
    bool RangeOverridden = false,
    string? ScheduleName = null,
    bool SchedulingEnabled = false,
    bool Paused = false,
    string? PauseReason = null);
