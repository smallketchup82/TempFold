// Copyright (c) 2026 smallketchup82 <ketchup@smkt.ca>. Licensed under the MIT License.
// See the LICENSE file in the repository root for full license text.

namespace TempFold.Control;

internal sealed class ScheduleService(TemperatureController controller, TimeProvider timeProvider) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        controller.RefreshSchedule();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken))
            controller.RefreshSchedule();
    }
}
