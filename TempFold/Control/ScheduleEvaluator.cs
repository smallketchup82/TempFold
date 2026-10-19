// Copyright (c) 2026 smallketchup82 <ketchup@smkt.ca>. Licensed under the MIT License.
// See the LICENSE file in the repository root for full license text.

using TempFold.Models;

namespace TempFold.Control;

internal sealed class ScheduleEvaluator
{
    private sealed record Block(string Name, DayOfWeek[] Days, TimeOnly Start, TimeOnly End, double Target, double Range);
    private readonly ControllerOptions options;
    private readonly TimeZoneInfo timeZone;
    private readonly Block[] blocks;

    internal ScheduleEvaluator(ControllerOptions options, TimeZoneInfo localTimeZone)
    {
        this.options = options;
        timeZone = options.TimeZone ?? localTimeZone;
        blocks = options.NoSchedule ? [] : (options.Schedule ?? [])
            .SelectMany(schedule => schedule.Blocks.Where(block => block.Enabled)
                .Select(block => new Block(schedule.Name,
                    schedule.Days.Select(day => Enum.Parse<DayOfWeek>(day, ignoreCase: true)).ToArray(),
                    block.Start, block.End, block.Target, block.Range))).ToArray();
    }

    internal TemperatureSettings Evaluate(DateTimeOffset utcNow)
    {
        DateTimeOffset local = TimeZoneInfo.ConvertTime(utcNow, timeZone);
        TimeOnly time = TimeOnly.FromDateTime(local.DateTime);
        DayOfWeek previousDay = local.AddDays(-1).DayOfWeek;
        foreach (Block block in blocks)
        {
            // Starts are inclusive, ends exclusive. Overnight blocks use the starting day.
            bool active = block.Start < block.End
                ? block.Days.Contains(local.DayOfWeek) && time >= block.Start && time < block.End
                : (block.Days.Contains(local.DayOfWeek) && time >= block.Start)
                  || (block.Days.Contains(previousDay) && time < block.End);
            if (active)
                return new(block.Target, block.Range, ScheduleName: block.Name, SchedulingEnabled: true);
        }
        return new(options.Target, options.Range,
            SchedulingEnabled: options.Schedule is not null && !options.NoSchedule);
    }
}
