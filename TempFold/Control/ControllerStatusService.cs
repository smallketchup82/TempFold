// Copyright (c) 2026 smallketchup82 <ketchup@smkt.ca>. Licensed under the MIT License.
// See the LICENSE file in the repository root for full license text.

using TempFold.Models;
using TempFold.Logging;
using System.Threading.Channels;

namespace TempFold.Control;

internal sealed class ControllerStatusService(TemperatureController controller, StatusConsole console, Integrations.FahService folding, ControllerOptions options) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Status.IntervalSeconds));
        using var ticking = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var refreshes = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
        {
            SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite
        });
        int sensorPending = 0;
        void OnReadingReceived()
        {
            // Ingest only signals the UI; it never waits for terminal rendering.
            Interlocked.Exchange(ref sensorPending, 1);
            refreshes.Writer.TryWrite(0);
        }
        void OnSettingsChanged() => refreshes.Writer.TryWrite(0);
        async Task TickAsync()
        {
            while (await timer.WaitForNextTickAsync(ticking.Token))
                refreshes.Writer.TryWrite(0);
        }
        if (console.EffectsEnabled)
            controller.ReadingReceived += OnReadingReceived;
        if (console.InputEnabled)
        {
            controller.SettingsChanged += OnSettingsChanged;
            folding.StatusChanged += OnSettingsChanged;
        }
        Task ticks = TickAsync();
        refreshes.Writer.TryWrite(0);
        try
        {
            while (await refreshes.Reader.WaitToReadAsync(stoppingToken))
            {
                while (refreshes.Reader.TryRead(out _)) { }
                WriteControllerStatus(Interlocked.Exchange(ref sensorPending, 0) != 0);
            }
        }
        finally
        {
            controller.ReadingReceived -= OnReadingReceived;
            controller.SettingsChanged -= OnSettingsChanged;
            folding.StatusChanged -= OnSettingsChanged;
            ticking.Cancel();
            refreshes.Writer.TryComplete();
            try { await ticks; }
            catch (OperationCanceledException) when (ticking.IsCancellationRequested) { }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await base.StopAsync(cancellationToken);
        }
        finally
        {
            console.Clear();
        }
    }

    private void WriteControllerStatus(bool sensorUpdate = false)
    {
        ControllerSnapshot snapshot = controller.GetSnapshot();
        TemperatureSettings settings = snapshot.Settings;
        DateTimeOffset now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, options.TimeZone ?? TimeZoneInfo.Local);
        if (options.Status.Json)
        {
            console.WriteReport(new StatusReport(now, snapshot.Temperature,
                snapshot.LastSensorUpdate,
                snapshot.Temperature is { } current5 ? GetTrendChange(snapshot.History, now, current5, 5) : null,
                snapshot.Temperature is { } current15 ? GetTrendChange(snapshot.History, now, current15, 15) : null,
                snapshot.Temperature is { } current60 ? GetTrendChange(snapshot.History, now, current60, 60) : null,
                snapshot.GameModeActive, snapshot.FoldingStatus is not null, snapshot.FoldingStatus?.IsWorking,
                settings.Target, settings.Range, settings.TargetOverridden, settings.RangeOverridden,
                settings.ScheduleName, settings.SchedulingEnabled, snapshot.Paused,
                snapshot.PauseReason?.ToString().ToLowerInvariant()));
            return;
        }
        console.Update(FormatStatus(now, snapshot.Temperature,
            snapshot.LastSensorUpdate, snapshot.History, snapshot.GameModeActive, snapshot.FoldingStatus,
            snapshot.FoldingStatus is not null, settings.Target, settings.Range, settings: settings,
            interactive: console.InputEnabled, pauseReason: snapshot.PauseReason), sensorUpdate);
    }

    internal static StatusLine FormatStatus(
        DateTimeOffset now, double? temperature,
        DateTimeOffset? lastSensorUpdate, IReadOnlyList<TemperatureReading> history,
        bool? gameModeActive, FoldingStatus? foldingStatus, bool connected,
        double target, double range, TemperatureGradient gradient = TemperatureGradient.Direct,
        TemperatureSettings? settings = null, bool interactive = false, PauseReason? pauseReason = null)
    {
        string updateAge = lastSensorUpdate is null
            ? "--s"
            : $"{Math.Max(0, (int)(now - lastSensorUpdate.Value).TotalSeconds)}s";
        StatusSegment[] clock = [new($"{now:HH:mm:ss} ({updateAge})", StatusColor.Muted),
            .. pauseReason is { } reason
                ? new[] { new StatusSegment(reason == PauseReason.Manual ? " PAUSED-MANUAL" : " PAUSED-GAMEMODE", StatusColor.RedColor) }
                : Array.Empty<StatusSegment>(),
            new(" | ", StatusColor.Muted)];

        StatusLine WithSettings(StatusLine line)
        {
            if (settings is null) return line.Prepend(clock);
            string targetText = $"{(interactive ? "[t]" : "target ")}{target:0.##}°{(settings.TargetOverridden ? " [override]" : "")}";
            string rangeText = $"{(interactive ? "[r]" : "range ")}±{range:0.##}°{(settings.RangeOverridden ? " [override]" : "")}";
            if (settings.SchedulingEnabled)
                line = line.Append(new StatusSegment($" | schedule: {settings.ScheduleName ?? "fallback"}"));
            if (interactive)
            {
                if (pauseReason is not null)
                    line = line.Append(new StatusSegment(" | [f]start [s]stop"));
                line = line.Append(new StatusSegment($" | [d]clear [p]{(pauseReason == PauseReason.Manual ? "unpause" : "pause")} [q]quit [?]help"));
            }
            return line.Prepend(new StatusSegment($"{targetText} {rangeText} | ")).Prepend(clock);
        }

        StatusSegment connection = new(connected ? "online" : "offline",
            connected ? StatusColor.GreenColor : StatusColor.RedColor);
        StatusSegment foldingActivity = new(FormatBooleanStatus(foldingStatus?.IsWorking), foldingStatus?.IsWorking == true
            ? StatusColor.GreenColor : StatusColor.Muted);
        if (temperature is null)
            return WithSettings(new StatusLine(new("FOLDING: "), foldingActivity,
                new(" | FAH: "), connection, new(" | Waiting for sensor data...")));

        return WithSettings(new StatusLine(
            new($"{temperature:F2} °C", StatusColor.ForTemperature(temperature.Value, target, range, gradient)),
            new(" | FOLDING: "), foldingActivity, new(" | FAH: "), connection,
            new(" | 5m "),
            FormatTrend(history, now, temperature.Value, 5),
            new(" · 15m "), FormatTrend(history, now, temperature.Value, 15),
            new(" · 60m "), FormatTrend(history, now, temperature.Value, 60)));
    }

    internal static StatusSegment FormatTrend(
        IReadOnlyList<TemperatureReading> history,
        DateTimeOffset now,
        double currentTemperature,
        int minutes)
    {
        double? change = GetTrendChange(history, now, currentTemperature, minutes);
        return change is { } value
            ? new StatusSegment($"{value:+0.00;-0.00;0.00}°", StatusColor.ForTrend(value))
            : new StatusSegment("n/a", StatusColor.Muted);
    }

    internal static double? GetTrendChange(IReadOnlyList<TemperatureReading> history,
        DateTimeOffset now, double currentTemperature, int minutes)
    {
        DateTimeOffset cutoff = now - TimeSpan.FromMinutes(minutes);
        TemperatureReading? baseline = history.LastOrDefault(reading => reading.Timestamp <= cutoff);

        if (baseline is null)
            return null;

        // Color the displayed value, including pure white when it rounds to zero.
        return Math.Round(currentTemperature - baseline.Temperature, 2);
    }

    internal static string FormatGameModeStatus(bool? isActive) =>
        isActive switch
        {
            true => "active",
            false => "inactive",
            null => "unknown"
        };

    internal static string FormatBooleanStatus(bool? value) =>
        value switch
        {
            true => "true",
            false => "false",
            null => "unknown"
        };

}
