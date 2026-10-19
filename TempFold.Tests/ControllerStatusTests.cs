// Copyright (c) 2026 smallketchup82 <ketchup@smkt.ca>. Licensed under the MIT License.
// See the LICENSE file in the repository root for full license text.

using System.Globalization;
using TempFold.Control;
using TempFold.Models;
using TempFold.Logging;

namespace TempFold.Tests;

public sealed class ControllerStatusTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ManualFoldingHintsAreShownOnlyWhilePaused(bool paused)
    {
        string status = ControllerStatusService.FormatStatus(
                DateTimeOffset.UtcNow,
                null,
                null,
                [],
                null,
                null,
                true,
                22,
                3,
                settings: new(22, 3),
                interactive: true,
                pauseReason: paused ? PauseReason.Manual : null)
            .Render(500, useColor: false);

        Assert.Equal(paused, status.Contains("[f]start"));
        Assert.Equal(paused, status.Contains("[s]stop"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(22.0)]
    public void ShowsEffectiveValuesOverridesAndActionsEvenBeforeFirstReading(double? temperature)
    {
        var settings = new TemperatureSettings(
            23,
            2,
            true,
            true,
            "weekday",
            true);
        string status = ControllerStatusService.FormatStatus(
                DateTimeOffset.UtcNow,
                temperature,
                null,
                [],
                null,
                null,
                true,
                23,
                2,
                settings: settings,
                interactive: true)
            .Render(500, useColor: false);

        Assert.Contains("[t]", status);
        Assert.Contains("[r]", status);
        Assert.Contains("[d]", status);
        Assert.Contains("[p]", status);
        Assert.Contains("[q]", status);
        Assert.Contains("[?]", status);
        Assert.Contains("23° [override]", status);
        Assert.Contains("±2° [override]", status);
        Assert.Contains("schedule: weekday", status);
    }

    [Theory]
    [InlineData((int)PauseReason.Manual, "PAUSED-MANUAL")]
    [InlineData((int)PauseReason.GameMode, "PAUSED-GAMEMODE")]
    public void PauseIndicatorIsRedBesideTheSensorAgeWhileFoldingRemainsClientReported(int reason, string label)
    {
        DateTimeOffset now = new(
            2026,
            1,
            1,
            12,
            0,
            0,
            TimeSpan.Zero);
        string status = ControllerStatusService.FormatStatus(
                now,
                22,
                now.AddSeconds(-3),
                [],
                true,
                new(true, true),
                true,
                22,
                3,
                pauseReason: (PauseReason)reason)
            .Render(500);

        Assert.Contains($"12:00:00 (3s)\u001b[0m\u001b[38;2;255;0;0m {label}", status);
        Assert.Contains("FOLDING: \u001b[38;2;0;255;0mtrue", status);
        Assert.DoesNotContain("GameMode:", status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    [InlineData(true)]
    public void NoPauseIndicatorOrGameModeFieldWithoutAnEffectiveInhibitor(bool? gameMode)
    {
        string status = ControllerStatusService.FormatStatus(
                DateTimeOffset.UtcNow,
                22,
                null,
                [],
                gameMode,
                null,
                false,
                22,
                3)
            .Render(500, useColor: false);

        Assert.DoesNotContain("GameMode", status);
        Assert.DoesNotContain("PAUSED", status);
        Assert.Contains("FOLDING: unknown", status);
    }

    [Fact]
    public void FoldingStatusIsShownWhileWaitingForTheFirstSensorReading()
    {
        string status = ControllerStatusService.FormatStatus(
                DateTimeOffset.UtcNow,
                null,
                null,
                [],
                null,
                new(true, true),
                true,
                22,
                3,
                pauseReason: PauseReason.Manual)
            .Render(500, useColor: false);

        Assert.Contains("PAUSED-MANUAL", status);
        Assert.Contains("FOLDING: true", status);
        Assert.Contains("Waiting for sensor data", status);
    }

    [Theory]
    [InlineData(true, "active", "true")]
    [InlineData(false, "inactive", "false")]
    [InlineData(null, "unknown", "unknown")]
    public void FormatsKnownAndUnknownStatuses(bool? value, string gameMode, string boolean)
    {
        Assert.Equal(gameMode, ControllerStatusService.FormatGameModeStatus(value));
        Assert.Equal(boolean, ControllerStatusService.FormatBooleanStatus(value));
    }

    [Fact]
    public void TrendNeedsReadingAtOrBeforeCutoff()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var unavailable = new StatusSegment("n/a", StatusColor.Muted);

        Assert.Equal(
            unavailable,
            ControllerStatusService.FormatTrend(
                [],
                now,
                22,
                5));
        Assert.Equal(
            unavailable,
            ControllerStatusService.FormatTrend(
                [new(now.AddMinutes(-4), 20)],
                now,
                22,
                5));
    }

    [Theory]
    [InlineData(22, "+1.00°")]
    [InlineData(20, "-1.00°")]
    [InlineData(21, "0.00°")]
    public void TrendUsesMostRecentReadingAtOrBeforeCutoff(double current, string expected)
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            DateTimeOffset now = DateTimeOffset.UtcNow;
            TemperatureReading[] history =
            [
                new(now.AddMinutes(-6), 10), new(now.AddMinutes(-5), 21), new(now.AddMinutes(-4), 30)
            ];

            Assert.Equal(
                expected,
                ControllerStatusService.FormatTrend(
                        history,
                        now,
                        current,
                        5)
                    .Text);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Theory]
    [InlineData(-0.004)]
    [InlineData(0)]
    [InlineData(0.004)]
    public void TrendDisplayedAsZeroIsWhite(double change)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        StatusSegment trend = ControllerStatusService.FormatTrend(
            [new(now.AddMinutes(-5), 0)],
            now,
            change,
            5);

        Assert.Equal(StatusColor.White, trend.Color);
        Assert.DoesNotContain('+', trend.Text);
        Assert.DoesNotContain('-', trend.Text);
    }

    [Fact]
    public void StatusLineColorsEachTrendIndependently()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        TemperatureReading[] history =
        [
            new(now.AddMinutes(-60), 27), new(now.AddMinutes(-15), 24), new(now.AddMinutes(-5), 21)
        ];
        string status = ControllerStatusService.FormatStatus(
                now,
                24,
                now,
                history,
                null,
                null,
                true,
                24,
                1)
            .Render(300);

        Assert.Contains("5m \u001b[38;2;255;0;0m+3", status);
        Assert.Contains("15m \u001b[38;2;255;255;255m0", status);
        Assert.Contains("60m \u001b[38;2;128;220;255m-3", status);
        Assert.DoesNotContain("Trends:", status);
    }
}