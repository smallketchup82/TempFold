// Copyright (c) 2026 smallketchup82 <ketchup@smkt.ca>. Licensed under the MIT License.
// See the LICENSE file in the repository root for full license text.

using TempFold.Integrations;
using TempFold.Models;

namespace TempFold.Control;

internal sealed class TemperatureController
{
    private readonly ControllerOptions options;
    private readonly ILogger<TemperatureController> logger;
    private long readingId;
    private long decisionId;
    private readonly Func<GameModeStatus> getGameModeStatus;
    private readonly Action<bool> setFolding;
    private readonly Action<bool> setPaused;
    private readonly Action<bool> setManualFolding;
    private readonly PauseState pause = new();
    private readonly Func<FoldingStatus?> getFoldingStatus;
    private readonly TimeProvider timeProvider;
    private readonly ScheduleEvaluator schedule;
    private TemperatureSettings settings;
    private double? targetOverride;
    private double? rangeOverride;

    public TemperatureController(ControllerOptions options, GameModeClient gameMode, FahService folding, ILogger<TemperatureController> logger)
        : this(options, gameMode.GetStatus, folding.SetFolding, folding.GetStatus, TimeProvider.System,
            folding.SetPaused, folding.SetFoldingManually, logger)
    {
    }

    internal TemperatureController(
        ControllerOptions options,
        Func<GameModeStatus> getGameModeStatus,
        Action<bool> setFolding,
        Func<FoldingStatus?> getFoldingStatus,
        TimeProvider timeProvider,
        Action<bool>? setPaused = null,
        Action<bool>? setManualFolding = null, ILogger<TemperatureController>? logger = null)
    {
        this.options = options;
        this.logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<TemperatureController>.Instance;
        this.getGameModeStatus = getGameModeStatus;
        this.setFolding = setFolding;
        this.setPaused = setPaused ?? (paused => { if (paused) setFolding(false); });
        this.setManualFolding = setManualFolding ?? setFolding;
        this.getFoldingStatus = getFoldingStatus;
        this.timeProvider = timeProvider;
        schedule = new ScheduleEvaluator(options, timeProvider.LocalTimeZone);
        settings = schedule.Evaluate(timeProvider.GetUtcNow());
        if (options.StartPaused)
        {
            pause.SetPaused(true, manual: true);
            this.setPaused(true);
        }
    }

    private bool? gameModeActive = null;
    private double? latestTemperature = null;
    private DateTimeOffset? lastSensorUpdate = null;
    private readonly List<TemperatureReading> temperatureHistory = new();
    private readonly object stateLock = new();
    public event Action? ReadingReceived;
    public event Action? SettingsChanged;

    public void ProcessReading(double temperature, string source = "unknown")
    {
        temperature = RoundSensorValue(temperature);

        bool stateChanged;
        lock (stateLock)
        {
            DateTimeOffset now = timeProvider.GetLocalNow();
            readingId++;
            latestTemperature = temperature;
            lastSensorUpdate = now;
            temperatureHistory.Add(new TemperatureReading(now, temperature));
            temperatureHistory.RemoveAll(reading => now - reading.Timestamp > TimeSpan.FromMinutes(61));

            PauseReason? previousPause = pause.Reason;
            stateChanged = UpdateSettings();
            UpdateGameModePause();
            stateChanged |= PublishPauseChange(previousPause);
            if (!pause.IsPaused)
                ApplyTemperature(temperature, resuming: previousPause is not null);
            LogDecision("controlDecision", temperature, source, previousPause is not null);
            LogDecision("sensorUpdate", temperature, source, previousPause is not null);
        }
        // Notify after publishing state and releasing the controller lock.
        ReadingReceived?.Invoke();
        if (stateChanged) SettingsChanged?.Invoke();
    }

    // Polls schedules and inhibitors independently of readings and status printing.
    public void RefreshSchedule() => ChangeSettings(() => { });

    public void Pause(bool paused, bool manual = false) => ChangePause(paused, manual);

    public void ToggleManualPause() => ChangePause(null, manual: true);

    public bool SetFoldingManually(bool folding)
    {
        lock (stateLock)
        {
            if (!pause.IsPaused)
            {
                logger.LogInformation("{logType}: ignored {action}, control is not paused", "userInput", folding ? "startFolding" : "stopFolding");
                return false;
            }
            logger.LogInformation("{logType}: manual folding request {action}", "userInput", folding ? "startFolding" : "stopFolding");
            setManualFolding(folding);
            return true;
        }
    }

    private void ChangePause(bool? requestedPause, bool manual)
    {
        bool changed;
        lock (stateLock)
        {
            logger.LogInformation("{logType}: {action}, manual {manual}", "userInput",
                (requestedPause ?? pause.Reason != PauseReason.Manual) ? "pause" : "unpause", manual);
            PauseReason? previousPause = pause.Reason;
            pause.SetPaused(requestedPause ?? pause.Reason != PauseReason.Manual, manual);
            if (manual && !pause.IsPaused)
                UpdateGameModePause();
            changed = PublishPauseChange(previousPause);
            if (changed && !pause.IsPaused && latestTemperature is { } temperature)
            {
                UpdateSettings();
                ApplyTemperature(temperature, resuming: true);
                LogDecision("controlDecision", temperature, "resume", true);
            }
        }
        if (changed) SettingsChanged?.Invoke();
    }

    internal void LogInput(string action, string? value = null) =>
        logger.LogInformation("{logType}: {action}, value {value}", "userInput", action, value);

    public void SetTargetOverride(double target)
    {
        if (!double.IsFinite(target)) throw new ArgumentOutOfRangeException(nameof(target));
        logger.LogInformation("{logType}: {action} {value}", "userInput", "setTarget", target);
        ChangeSettings(() => targetOverride = target == options.Target ? null : target);
    }

    public void SetRangeOverride(double range)
    {
        if (!double.IsFinite(range) || range <= 0) throw new ArgumentOutOfRangeException(nameof(range));
        logger.LogInformation("{logType}: {action} {value}", "userInput", "setRange", range);
        ChangeSettings(() => rangeOverride = range == options.Range ? null : range);
    }

    public void ClearOverrides()
    {
        logger.LogInformation("{logType}: {action}", "userInput", "clearOverrides");
        ChangeSettings(() => { targetOverride = null; rangeOverride = null; });
    }

    private void ChangeSettings(Action change)
    {
        bool changed;
        lock (stateLock)
        {
            change();
            changed = UpdateSettings();
            PauseReason? previousPause = pause.Reason;
            UpdateGameModePause();
            changed |= PublishPauseChange(previousPause);
            if (changed && latestTemperature is { } temperature)
            {
                if (!pause.IsPaused) ApplyTemperature(temperature, resuming: previousPause is not null);
                LogDecision("controlDecision", temperature, "settingsOrPause", previousPause is not null);
            }
        }
        if (changed) SettingsChanged?.Invoke();
    }

    private bool UpdateSettings()
    {
        TemperatureSettings current = schedule.Evaluate(timeProvider.GetUtcNow()) with
        {
            TargetOverridden = targetOverride.HasValue,
            RangeOverridden = rangeOverride.HasValue
        };
        current = current with { Target = targetOverride ?? current.Target, Range = rangeOverride ?? current.Range };
        if (current == settings) return false;
        logger.LogInformation("{logType}: target {target}, range {range}, schedule {schedule}",
            "settingsChanged", current.Target, current.Range, current.ScheduleName);
        settings = current;
        return true;
    }

    private void UpdateGameModePause()
    {
        GameModeStatus gameModeStatus = getGameModeStatus();
        if (gameModeStatus.IsActive is not null)
            gameModeActive = gameModeStatus.IsActive;
        pause.SetPaused(options.PauseOnGameMode && gameModeActive == true);
    }

    private bool PublishPauseChange(PauseReason? previousPause)
    {
        if (previousPause == pause.Reason) return false;
        logger.LogInformation("{logType}: paused {paused}, reason {pauseReason}", "pauseChanged",
            pause.IsPaused, pause.Reason?.ToString().ToLowerInvariant());
        setPaused(pause.IsPaused);
        return true;
    }

    private void ApplyTemperature(double temperature, bool resuming = false)
    {
        // Hysteresis preserves the service's previous intent in the middle band.
        if (temperature <= settings.Target - settings.Range
                 || (resuming && temperature < settings.Target + settings.Range))
            setFolding(true);
        else if (temperature >= settings.Target + settings.Range)
            setFolding(false);
    }

    private void LogDecision(string type, double temperature, string source, bool resuming)
    {
        if (!logger.IsEnabled(type == "sensorUpdate" ? LogLevel.Trace : LogLevel.Debug)) return;
        bool? desired = temperature <= settings.Target - settings.Range || (resuming && temperature < settings.Target + settings.Range)
            ? true : temperature >= settings.Target + settings.Range ? false : null;
        FoldingStatus? actual = getFoldingStatus();
        string decision = pause.IsPaused ? "inhibited" : desired is null ? "withinBand"
            : actual is null ? "awaitingConnection" : actual.IsConfiguredToFold == desired ? "alreadyMatching"
            : desired.Value ? "requestStart" : "requestStop";
        logger.Log(type == "sensorUpdate" ? LogLevel.Trace : LogLevel.Debug,
            "{logType}: reading {readingId}, decision {decisionId}, source {source}, temperature {temperature}, target {target}, range {range}, start {startThreshold}, stop {stopThreshold}, paused {paused}, reason {pauseReason}, connected {fahConnected}, folding {folding}, desired {desiredFolding}, outcome {decision}",
            type, readingId, ++decisionId, source, temperature, settings.Target, settings.Range,
            settings.Target - settings.Range, settings.Target + settings.Range, pause.IsPaused,
            pause.Reason?.ToString().ToLowerInvariant(), actual is not null, actual?.IsConfiguredToFold, desired, decision);
    }

    public ControllerSnapshot GetSnapshot()
    {
        lock (stateLock)
        {
            return new ControllerSnapshot(
                latestTemperature,
                lastSensorUpdate,
                temperatureHistory.ToArray(),
                gameModeActive,
                getFoldingStatus()) { Settings = settings, PauseReason = pause.Reason };
        }
    }

    private static double RoundSensorValue(double value) => Math.Round(value * 20, MidpointRounding.AwayFromZero) / 20;
}
