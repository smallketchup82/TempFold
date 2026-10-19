// Copyright (c) 2026 smallketchup82 <ketchup@smkt.ca>. Licensed under the MIT License.
// See the LICENSE file in the repository root for full license text.

using TempFold.Integrations;
using TempFold.Sensors;

namespace TempFold.Control;

// Deadlines use monotonic time and are independent of status output frequency.
internal sealed class RuntimeLifetimeService(
    ControllerOptions options,
    SensorInputService input,
    FahService folding,
    IHostApplicationLifetime lifetime,
    TimeProvider timeProvider,
    ILogger<RuntimeLifetimeService> logger) : BackgroundService
{
    private readonly Lock stateLock = new();
    private long lastReading;
    private long? disconnectedSince;

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        lastReading = timeProvider.GetTimestamp();
        disconnectedSince = folding.IsConnected ? null : lastReading;
        input.DataReceived += OnReading;
        folding.ConnectionChanged += OnConnectionChanged;
        return base.StartAsync(cancellationToken);
    }

    private void OnReading()
    {
        lock (stateLock) lastReading = timeProvider.GetTimestamp();
    }

    private void OnConnectionChanged(bool connected)
    {
        if (lifetime.ApplicationStopping.IsCancellationRequested) return;
        lock (stateLock) disconnectedSince = connected ? null : timeProvider.GetTimestamp();
        if (!connected && options.QuitOnFahDisconnect)
        {
            logger.LogWarning("Folding@home disconnected; exiting because quitOnFahDisconnect is enabled.");
            lifetime.StopApplication();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100), timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            string? reason = null;
            lock (stateLock)
            {
                if (options.SensorTimeoutSeconds > 0
                    && timeProvider.GetElapsedTime(lastReading).TotalSeconds >= options.SensorTimeoutSeconds)
                    reason = $"No valid ESP32 data for {options.SensorTimeoutSeconds} seconds.";
                else if (options.FahConnectionTimeoutSeconds > 0 && disconnectedSince is { } since
                    && timeProvider.GetElapsedTime(since).TotalSeconds >= options.FahConnectionTimeoutSeconds)
                    reason = $"No Folding@home connection for {options.FahConnectionTimeoutSeconds} seconds.";
            }
            if (reason is null) continue;
            logger.LogWarning("{Reason} Exiting TempFold.", reason);
            lifetime.StopApplication();
            return;
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        input.DataReceived -= OnReading;
        folding.ConnectionChanged -= OnConnectionChanged;
        await base.StopAsync(cancellationToken);
    }
}
