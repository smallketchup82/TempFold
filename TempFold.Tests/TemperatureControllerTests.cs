// Copyright (c) 2026 smallketchup82 <ketchup@smkt.ca>. Licensed under the MIT License.
// See the LICENSE file in the repository root for full license text.

using TempFold.Control;
using TempFold.Models;

namespace TempFold.Tests;

public sealed class TemperatureControllerTests
{
    private readonly List<bool> commands = [];
    private GameModeStatus gameMode = new(false, null);
    private FoldingStatus? foldingStatus;
    private readonly Clock clock = new();
    private readonly TemperatureController controller;

    public TemperatureControllerTests() =>
        controller = new TemperatureController(
            new ControllerOptions(
                22,
                3,
                "unused",
                115200,
                new Uri("ws://localhost/api/websocket")),
            () => gameMode,
            commands.Add,
            () => foldingStatus,
            clock);

    [Fact]
    public void EveryReadingNotifiesAfterPublishingEvenWhenValuesAndTimestampAreIdentical()
    {
        var snapshots = new List<ControllerSnapshot>();

        controller.ReadingReceived += () => snapshots.Add(controller.GetSnapshot());
        controller.ProcessReading(22);
        controller.ProcessReading(22);

        Assert.Equal(2, snapshots.Count);
        Assert.Equal(22, snapshots[0].Temperature);
        Assert.Single(snapshots[0].History);
        Assert.Equal(2, snapshots[1].History.Count);
        Assert.Equal(snapshots[0].LastSensorUpdate, snapshots[1].LastSensorUpdate);
    }

    [Fact]
    public void InitialSnapshotHasNoReadingsOrKnownStatuses()
    {
        ControllerSnapshot snapshot = controller.GetSnapshot();

        Assert.Null(snapshot.Temperature);
        Assert.Null(snapshot.LastSensorUpdate);
        Assert.Null(snapshot.GameModeActive);
        Assert.Null(snapshot.FoldingStatus);
        Assert.Empty(snapshot.History);
    }

    [Theory]
    [InlineData(18, true)]
    [InlineData(19, true)]
    [InlineData(25, false)]
    [InlineData(26, false)]
    public void CommandsFoldingAtInclusiveThresholds(double temperature, bool expected)
    {
        controller.ProcessReading(temperature);

        Assert.Equal(expected, Assert.Single(commands));
    }

    [Fact]
    public void MiddleBandPreservesPreviousIntent()
    {
        controller.ProcessReading(22);

        Assert.Empty(commands);

        controller.ProcessReading(19);
        controller.ProcessReading(22);
        controller.ProcessReading(25);
        controller.ProcessReading(22);

        Assert.Equal(new[] { true, false }, commands);
    }

    [Fact]
    public void GameModePausesEvenWhenColdAndRemembersActiveStatusOnFailure()
    {
        gameMode = new(true, null);
        controller.ProcessReading(18);
        gameMode = new(null, "unavailable");
        controller.ProcessReading(18);

        Assert.Equal(new[] { false }, commands);
        Assert.True(controller.GetSnapshot().GameModeActive);
        Assert.Equal(PauseReason.GameMode, controller.GetSnapshot().PauseReason);
    }

    [Fact]
    public void ManualPauseWinsOverAutomaticSourcesAndAllowsReportedFoldingToChange()
    {
        controller.ProcessReading(18);
        controller.Pause(true, manual: true);

        Assert.Equal(new[] { true, false }, commands);

        foldingStatus = new(true, true); // An external source starts FAH again.
        gameMode = new(true, null);
        controller.RefreshSchedule();
        controller.Pause(false); // Automatic unpause cannot lift the manual pause.
        controller.Pause(true);
        controller.ProcessReading(18);
        controller.ProcessReading(30);
        gameMode = new(false, null);
        controller.RefreshSchedule();

        Assert.Equal(new[] { true, false }, commands);
        Assert.Equal(PauseReason.Manual, controller.GetSnapshot().PauseReason);
        Assert.Equal(new FoldingStatus(true, true), controller.GetSnapshot().FoldingStatus);
    }

    [Theory]
    [InlineData(18, true)]
    [InlineData(22, true)]
    [InlineData(24.95, true)]
    [InlineData(25, false)]
    [InlineData(30, false)]
    public void ManualResumeRechecksTheLatestTemperatureAndResumesBelowTheStopThreshold(
        double temperature,
        bool expected)
    {
        controller.Pause(true, manual: true);
        controller.ProcessReading(temperature);

        Assert.False(Assert.Single(commands));

        controller.Pause(false, manual: true);

        Assert.Equal(expected, commands.Last());
        Assert.False(controller.GetSnapshot().Paused);
    }

    [Fact]
    public void ReleasingManualPauseKeepsActiveGameModePauseWithoutAnyStart()
    {
        controller.Pause(true, manual: true);
        controller.ProcessReading(18);
        gameMode = new(true, null);
        controller.ToggleManualPause();

        Assert.Equal(PauseReason.GameMode, controller.GetSnapshot().PauseReason);
        Assert.All(commands, command => Assert.False(command));

        gameMode = new(false, null);
        controller.RefreshSchedule();

        Assert.True(commands.Last());
        Assert.False(controller.GetSnapshot().Paused);
    }

    [Fact]
    public void PausingBeforeAnyReadingStopsOnceAndResumingDoesNotInventATemperature()
    {
        controller.ToggleManualPause();
        controller.RefreshSchedule();
        controller.ToggleManualPause();

        Assert.False(Assert.Single(commands));
        Assert.False(controller.GetSnapshot().Paused);
        Assert.Empty(controller.GetSnapshot().History);
    }

    [Fact]
    public void GameModeIsPolledAndReleasedWithoutNewSensorReadings()
    {
        controller.ProcessReading(22);
        gameMode = new(true, null);
        controller.RefreshSchedule();

        Assert.False(Assert.Single(commands));

        gameMode = new(false, null);
        controller.RefreshSchedule();

        Assert.Equal(new[] { false, true }, commands);
        Assert.Single(controller.GetSnapshot().History);
    }

    [Theory]
    [InlineData(22, true)]
    [InlineData(25, false)]
    public void GameModeStoppingResumesOnlyBelowStopThreshold(double temperature, bool expected)
    {
        gameMode = new(true, null);
        controller.ProcessReading(temperature);
        gameMode = new(false, null);
        controller.ProcessReading(temperature);

        Assert.Equal(new[] { false, expected }, commands);
    }

    [Fact]
    public void UnknownGameModeInitiallyAllowsTemperatureControl()
    {
        gameMode = new(null, "unavailable");
        controller.ProcessReading(18);

        Assert.True(Assert.Single(commands));
        Assert.Null(controller.GetSnapshot().GameModeActive);
    }

    [Fact]
    public void DisabledGameModePauseKeepsTemperatureHysteresisAcrossGameModeTransitions()
    {
        var options = new ControllerOptions(
            22,
            3,
            "unused",
            115200,
            new Uri("ws://localhost/api/websocket")) { PauseOnGameMode = false };
        var unpaused = new TemperatureController(
            options,
            () => gameMode,
            commands.Add,
            () => null,
            clock);

        gameMode = new(true, null);
        unpaused.ProcessReading(18);
        unpaused.ProcessReading(26);
        unpaused.ProcessReading(22);
        gameMode = new(false, null);
        unpaused.ProcessReading(22);

        Assert.Equal(new[] { true, false }, commands);
        Assert.False(unpaused.GetSnapshot().GameModeActive);
    }

    [Theory]
    [InlineData(22.024, 22.0)]
    [InlineData(22.025, 22.05)]
    [InlineData(-22.025, -22.05)]
    public void RoundsReadingsToNearestTwentiethAwayFromZero(double value, double expected)
    {
        controller.ProcessReading(value);
        ControllerSnapshot snapshot = controller.GetSnapshot();

        Assert.Equal(expected, snapshot.Temperature);
        Assert.Equal(expected, Assert.Single(snapshot.History).Temperature);
        Assert.Equal(clock.GetUtcNow(), snapshot.LastSensorUpdate);
    }

    [Fact]
    public void UsesRoundedTemperatureForThresholdDecision()
    {
        controller.ProcessReading(19.024);
        controller.ProcessReading(24.976);

        Assert.Equal(new[] { true, false }, commands);
    }

    [Fact]
    public void HistoryRetainsBoundaryAndPrunesOlderReadingsWithoutChangingPreviousSnapshot()
    {
        controller.ProcessReading(20);
        ControllerSnapshot original = controller.GetSnapshot();

        clock.Advance(TimeSpan.FromMinutes(61));
        controller.ProcessReading(21);

        Assert.Equal(2, controller.GetSnapshot().History.Count);

        clock.Advance(TimeSpan.FromTicks(1));
        controller.ProcessReading(22);

        Assert.Equal(new[] { 21.0, 22.0 }, controller.GetSnapshot().History.Select(x => x.Temperature));
        Assert.Equal(20, Assert.Single(original.History).Temperature);
    }

    [Fact]
    public void SnapshotReflectsLatestFoldingStatus()
    {
        foldingStatus = new(true, false);

        Assert.Equal(foldingStatus, controller.GetSnapshot().FoldingStatus);

        foldingStatus = null;

        Assert.Null(controller.GetSnapshot().FoldingStatus);
    }

    [Fact]
    public void ReturningToStartupValuesClearsOverridesIndependentlyAndReevaluatesFolding()
    {
        controller.ProcessReading(25);

        Assert.False(commands.Last());

        controller.SetTargetOverride(30);
        controller.SetRangeOverride(1);

        Assert.True(commands.Last());
        Assert.True(controller.GetSnapshot().Settings.TargetOverridden);
        Assert.True(controller.GetSnapshot().Settings.RangeOverridden);

        controller.SetTargetOverride(22);
        var targetRestored = controller.GetSnapshot().Settings;

        Assert.False(targetRestored.TargetOverridden);
        Assert.True(targetRestored.RangeOverridden);
        Assert.False(commands.Last());

        controller.SetRangeOverride(3);
        var restored = controller.GetSnapshot().Settings;

        Assert.Equal(22, restored.Target);
        Assert.Equal(3, restored.Range);
        Assert.False(restored.TargetOverridden);
        Assert.False(restored.RangeOverridden);
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = new(
            2026,
            1,
            1,
            0,
            0,
            0,
            TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public void Advance(TimeSpan amount) => now += amount;
    }
}