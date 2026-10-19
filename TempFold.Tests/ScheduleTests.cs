// Copyright (c) 2026 smallketchup82 <ketchup@smkt.ca>. Licensed under the MIT License.
// See the LICENSE file in the repository root for full license text.

using TempFold.Configuration;
using TempFold.Control;
using TempFold.Models;

namespace TempFold.Tests;

public sealed class ScheduleTests
{
    [Theory]
    [InlineData("2026-01-05T18:59:59Z", 20)]
    [InlineData("2026-01-05T19:00:00Z", 23)]
    [InlineData("2026-01-05T20:59:59Z", 23)]
    [InlineData("2026-01-05T21:00:00Z", 20)]
    [InlineData("2026-07-06T18:00:00Z", 23)]
    [InlineData("2026-01-06T19:00:00Z", 20)]
    public void UsesLocalWeekdayAndWallClockWithSeasonalOffsetsAndExclusiveEnd(string now, double target)
    {
        var evaluator = new ScheduleEvaluator(
            Options(
                new Schedule(
                    "afternoon",
                    ["monday"],
                    [
                        new(
                            "14:00",
                            "16:00",
                            23,
                            2)
                    ])),
            TimeZoneInfo.Utc);
        TemperatureSettings settings = evaluator.Evaluate(DateTimeOffset.Parse(now));

        Assert.Equal(target, settings.Target);
        Assert.Equal(target == 23 ? "afternoon" : null, settings.ScheduleName);
    }

    [Theory]
    [InlineData("2026-01-05T03:30:00Z", 23)] // Sunday 22:30.
    [InlineData("2026-01-05T06:59:59Z", 23)] // Monday 01:59.
    [InlineData("2026-01-05T07:00:00Z", 20)]
    [InlineData("2026-01-06T03:30:00Z", 20)] // Monday night isn't Sunday.
    public void OvernightBlocksFollowTheirStartingDayIncludingWeekRollover(string now, double target)
    {
        var evaluator = new ScheduleEvaluator(
            Options(
                new Schedule(
                    "overnight",
                    ["sunday"],
                    [
                        new(
                            "22:00",
                            "2:00",
                            23,
                            2)
                    ])),
            TimeZoneInfo.Utc);

        Assert.Equal(target, evaluator.Evaluate(DateTimeOffset.Parse(now)).Target);
    }

    [Theory]
    [InlineData("2026-11-01T05:30:00Z", 23)] // First 01:30.
    [InlineData("2026-11-01T06:30:00Z", 23)] // Repeated 01:30.
    [InlineData("2026-03-08T06:59:59Z", 23)]
    [InlineData("2026-03-08T07:00:00Z", 23)] // Clock jumps to 03:00.
    public void DstFollowsWallClockDuringRepeatedAndSkippedHours(string now, double target)
    {
        var evaluator = new ScheduleEvaluator(
            Options(
                new Schedule(
                    "DST",
                    ["sunday"],
                    [
                        new(
                            "1:00",
                            "2:00",
                            23,
                            2),
                        new(
                            "2:30",
                            "3:30",
                            23,
                            2)
                    ])),
            TimeZoneInfo.Utc);

        Assert.Equal(target, evaluator.Evaluate(DateTimeOffset.Parse(now)).Target);
    }

    [Fact]
    public void FirstEnabledMatchWinsAndOmittedTimezoneUsesTheUsersLocalZone()
    {
        var options = Options(
                new(
                    "first",
                    ["Monday"],
                    [
                        new(
                            "14:00",
                            "16:00",
                            99,
                            1,
                            false),
                        new(
                            "14:00",
                            "16:00",
                            23,
                            2)
                    ]),
                new(
                    "second",
                    ["monday"],
                    [
                        new(
                            "14:00",
                            "16:00",
                            25,
                            1)
                    ])) with
            {
                TimeZone = null
            };
        var evaluator = new ScheduleEvaluator(options, AppConfig.ResolveTimeZone("America/Toronto"));

        Assert.Equal("first", evaluator.Evaluate(DateTimeOffset.Parse("2026-01-05T19:00:00Z")).ScheduleName);
        Assert.Equal(23, evaluator.Evaluate(DateTimeOffset.Parse("2026-01-05T19:00:00Z")).Target);

        var disabled = new ScheduleEvaluator(options with { NoSchedule = true }, TimeZoneInfo.Utc);

        Assert.Equal(new TemperatureSettings(20, 3), disabled.Evaluate(DateTimeOffset.Parse("2026-01-05T19:00:00Z")));
    }

    [Fact]
    public void ScheduleChangesAndClearingOverridesReevaluateLatestReadingWithoutAddingSensorHistory()
    {
        var clock = new Clock(DateTimeOffset.Parse("2026-01-05T18:59:00Z"));
        var commands = new List<bool>();
        var options = Options(
            new Schedule(
                "afternoon",
                ["monday"],
                [
                    new(
                        "14:00",
                        "16:00",
                        25,
                        2)
                ]));
        var controller = new TemperatureController(
            options,
            () => new(false, null),
            commands.Add,
            () => null,
            clock);

        controller.ProcessReading(23); // Fallback stop threshold = 23.

        Assert.False(Assert.Single(commands));

        ControllerSnapshot before = controller.GetSnapshot();

        clock.Now = DateTimeOffset.Parse("2026-01-05T19:00:00Z");
        controller.RefreshSchedule(); // New start threshold = 23.

        Assert.True(commands.Last());
        Assert.Equal(25, controller.GetSnapshot().Settings.Target);
        Assert.Equal(before.LastSensorUpdate, controller.GetSnapshot().LastSensorUpdate);
        Assert.Single(controller.GetSnapshot().History);

        controller.SetTargetOverride(21);

        Assert.False(commands.Last());

        controller.SetRangeOverride(1);
        clock.Now = DateTimeOffset.Parse("2026-01-05T21:00:00Z");
        controller.RefreshSchedule();

        Assert.Equal(
            new TemperatureSettings(
                21,
                1,
                true,
                true,
                SchedulingEnabled: true),
            controller.GetSnapshot().Settings);

        controller.ClearOverrides();

        Assert.Equal(new TemperatureSettings(20, 3, SchedulingEnabled: true), controller.GetSnapshot().Settings);
    }

    [Fact]
    public void IndependentOverridesKeepFollowingTheUnmodifiedScheduledValueAndRespectGameMode()
    {
        var clock = new Clock(DateTimeOffset.Parse("2026-01-05T19:00:00Z"));
        var commands = new List<bool>();
        var controller = new TemperatureController(
            Options(
                new Schedule(
                    "afternoon",
                    ["monday"],
                    [
                        new(
                            "14:00",
                            "15:00",
                            25,
                            2),
                        new(
                            "15:00",
                            "16:00",
                            27,
                            1)
                    ])),
            () => new(true, null),
            commands.Add,
            () => null,
            clock);

        controller.SetTargetOverride(30);
        controller.ProcessReading(20);
        clock.Now = DateTimeOffset.Parse("2026-01-05T20:00:00Z");
        controller.RefreshSchedule();

        Assert.Equal(
            new TemperatureSettings(
                30,
                1,
                true,
                ScheduleName: "afternoon",
                SchedulingEnabled: true),
            controller.GetSnapshot().Settings);

        controller.ClearOverrides();

        Assert.Equal(27, controller.GetSnapshot().Settings.Target);

        controller.SetRangeOverride(0.5);
        clock.Now = DateTimeOffset.Parse("2026-01-05T21:00:00Z");
        controller.RefreshSchedule();

        Assert.Equal(
            new TemperatureSettings(
                20,
                0.5,
                RangeOverridden: true,
                SchedulingEnabled: true),
            controller.GetSnapshot().Settings);
        Assert.All(commands, command => Assert.False(command));
    }

    [Fact]
    public async Task HostedScheduleLoopUpdatesWithoutAnySensorOrStatusLoop()
    {
        var clock = new Clock(DateTimeOffset.Parse("2026-01-05T18:59:00Z"));
        var controller = new TemperatureController(
            Options(
                new Schedule(
                    "afternoon",
                    ["monday"],
                    [
                        new(
                            "14:00",
                            "16:00",
                            25,
                            2)
                    ])),
            () => new(false, null),
            _ =>
            {
            },
            () => null,
            clock);
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        controller.SettingsChanged += () => changed.TrySetResult();
        using var service = new ScheduleService(controller, clock);

        await service.StartAsync(CancellationToken.None);
        try
        {
            await clock.TimerCreated.Task.WaitAsync(TimeSpan.FromSeconds(3));
            clock.Now = DateTimeOffset.Parse("2026-01-05T19:00:00Z");
            await changed.Task.WaitAsync(TimeSpan.FromSeconds(3));

            Assert.Equal(25, controller.GetSnapshot().Settings.Target);
            Assert.Empty(controller.GetSnapshot().History);
            Assert.Null(controller.GetSnapshot().LastSensorUpdate);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    private static ControllerOptions Options(params Schedule[] schedules) =>
        new(
            20,
            3,
            "unused",
            115200,
            new Uri("ws://localhost/api/websocket"))
        {
            Schedule = schedules, TimeZone = AppConfig.ResolveTimeZone("America/Toronto")
        };

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        internal DateTimeOffset Now = now;
        internal TaskCompletionSource TimerCreated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override DateTimeOffset GetUtcNow() => Now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            ITimer timer = base.CreateTimer(
                callback,
                state,
                dueTime,
                period);
            TimerCreated.TrySetResult();
            return timer;
        }
    }
}