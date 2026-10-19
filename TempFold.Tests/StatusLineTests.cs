// Copyright (c) 2026 smallketchup82 <ketchup@smkt.ca>. Licensed under the MIT License.
// See the LICENSE file in the repository root for full license text.

using TempFold.Logging;
using TempFold.Control;

namespace TempFold.Tests;

public sealed class StatusLineTests
{
    [Theory]
    [InlineData(
        -5,
        128,
        220,
        255)]
    [InlineData(
        -3,
        128,
        220,
        255)]
    [InlineData(
        -1.5,
        192,
        238,
        255)]
    [InlineData(
        0,
        255,
        255,
        255)]
    [InlineData(
        1.5,
        255,
        128,
        128)]
    [InlineData(
        3,
        255,
        0,
        0)]
    [InlineData(
        5,
        255,
        0,
        0)]
    public void TrendGradientBlendsFromWhiteAndSaturatesAtThreeDegrees(
        double change,
        int red,
        int green,
        int blue)
    {
        Assert.Equal(new StatusColor(red, green, blue), StatusColor.ForTrend(change));
    }

    [Fact]
    public void DefaultGradientUsesThePreferredNarrowGreenOverlay()
    {
        Assert.Equal(new StatusColor(128, 250, 123), StatusColor.ForTemperature(24.25, 24, 2));

        DateTimeOffset now = DateTimeOffset.UtcNow;
        string status = ControllerStatusService.FormatStatus(
                now,
                24.25,
                now,
                [],
                null,
                null,
                true,
                24,
                2)
            .Render(200);

        Assert.Contains("\u001b[38;2;128;250;123m24", status);
    }

    [Fact]
    public void SpectreWritesLiteralTextAndSupportsUncoloredOutput()
    {
        var line = new StatusLine(new StatusSegment("[green]literal[/]", StatusColor.IceBlue));

        Assert.Equal("\u001b[38;2;128;220;255m[green]literal[/]\u001b[0m", line.Render(100));
        Assert.Equal("[green]literal[/]", line.Render(100, useColor: false));
    }

    [Theory]
    [InlineData(10, 2)]
    [InlineData(-5, 0.5)]
    [InlineData(32, 4)]
    public void DirectGradientHasGreenAtTargetAndSaturatesBeyondConfiguredBounds(double target, double range)
    {
        StatusColor Color(double temperature) =>
            StatusColor.ForTemperature(
                temperature,
                target,
                range,
                TemperatureGradient.Direct);

        Assert.Equal(StatusColor.GreenColor, Color(target));
        Assert.Equal(new StatusColor(192, 238, 255), Color(target - (range + 5) / 2));
        Assert.Equal(new StatusColor(255, 128, 128), Color(target + (range + 5) / 2));
        Assert.NotEqual(StatusColor.White, Color(target - range));
        Assert.NotEqual(StatusColor.White, Color(target + range));
        Assert.Equal(StatusColor.IceBlue, Color(target - range - 5));
        Assert.Equal(StatusColor.RedColor, Color(target + range + 5));
        Assert.Equal(StatusColor.IceBlue, Color(target - range - 50));
        Assert.Equal(StatusColor.RedColor, Color(target + range + 50));
    }

    [Fact]
    public void StatusFormatterCanPreviewDirectGradient()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string status = ControllerStatusService.FormatStatus(
                now,
                27,
                now,
                [],
                null,
                null,
                true,
                24,
                1,
                TemperatureGradient.Direct)
            .Render(200);

        Assert.Contains("\u001b[38;2;255;128;128m27", status);

        string atTarget = ControllerStatusService.FormatStatus(
                now,
                24,
                now,
                [],
                null,
                null,
                true,
                24,
                1,
                TemperatureGradient.Direct)
            .Render(200);

        Assert.Contains("\u001b[38;2;0;255;0m24", atTarget);
    }

    [Theory]
    [InlineData(
        23.499999,
        246,
        252,
        255)]
    [InlineData(
        23.5,
        246,
        252,
        255)]
    [InlineData(
        23.500001,
        246,
        253,
        255)]
    [InlineData(
        23.75,
        125,
        254,
        128)]
    [InlineData(
        24,
        0,
        255,
        0)]
    [InlineData(
        24.25,
        128,
        250,
        123)]
    [InlineData(
        24.499999,
        255,
        237,
        237)]
    [InlineData(
        24.5,
        255,
        237,
        237)]
    [InlineData(
        24.500001,
        255,
        237,
        237)]
    public void DirectGreenOverlayIsNarrowAndContinuous(
        double temperature,
        int red,
        int green,
        int blue)
    {
        Assert.Equal(
            new StatusColor(red, green, blue),
            StatusColor.ForTemperature(
                temperature,
                24,
                2,
                TemperatureGradient.Direct));
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(2)]
    [InlineData(4)]
    public void DirectGreenOverlayScalesWithRange(double range)
    {
        // Above the green zone, the red-side baseline has equal green/blue channels.
        StatusColor inside = StatusColor.ForTemperature(
            24 + range / 8,
            24,
            range,
            TemperatureGradient.Direct);
        StatusColor outside = StatusColor.ForTemperature(
            24 + range / 4,
            24,
            range,
            TemperatureGradient.Direct);

        Assert.True(inside.Green > inside.Blue);
        Assert.Equal(outside.Green, outside.Blue);
    }

    [Fact]
    public void StatusFormatterColorsTemperatureUsingItsConfiguration()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string atTarget = ControllerStatusService.FormatStatus(
                now,
                10,
                now,
                [],
                null,
                null,
                false,
                10,
                2)
            .Render(200);

        Assert.Contains("\u001b[38;2;0;255;0m10", atTarget);
        Assert.Contains("\u001b[38;2;255;0;0moffline\u001b[0m", atTarget);

        string aboveBound = ControllerStatusService.FormatStatus(
                now,
                17,
                now,
                [],
                null,
                null,
                false,
                10,
                2)
            .Render(200);

        Assert.Contains("\u001b[38;2;255;0;0m17", aboveBound);
    }

    [Fact]
    public void MissingSensorDataStillShowsColoredConnectionStatus()
    {
        string status = ControllerStatusService.FormatStatus(
                DateTimeOffset.UtcNow,
                null,
                null,
                [],
                null,
                null,
                true,
                10,
                2)
            .Render(200);

        Assert.Contains("Waiting for sensor data...", status);
        Assert.Contains("\u001b[38;2;0;255;0monline\u001b[0m", status);
    }

    [Theory]
    [InlineData(
        0,
        128,
        220,
        255)]
    [InlineData(
        18,
        128,
        220,
        255)]
    [InlineData(
        20.5,
        192,
        238,
        255)]
    [InlineData(
        23,
        255,
        255,
        255)]
    [InlineData(
        23.5,
        128,
        255,
        128)]
    [InlineData(
        24,
        0,
        255,
        0)]
    [InlineData(
        24.5,
        128,
        255,
        128)]
    [InlineData(
        25,
        255,
        255,
        255)]
    [InlineData(
        27.5,
        255,
        128,
        128)]
    [InlineData(
        30,
        255,
        0,
        0)]
    [InlineData(
        40,
        255,
        0,
        0)]
    public void TemperatureGradientHasExpectedAnchorsAndMidpoints(
        double temperature,
        int red,
        int green,
        int blue)
    {
        Assert.Equal(
            new StatusColor(red, green, blue),
            StatusColor.ForTemperature(
                temperature,
                24,
                1,
                TemperatureGradient.WhiteBounds));
    }

    [Theory]
    [InlineData(10, 2)]
    [InlineData(-5, 0.5)]
    [InlineData(32, 4)]
    public void GradientUsesConfiguredTargetAndRange(double target, double range)
    {
        StatusColor Color(double temperature) =>
            StatusColor.ForTemperature(
                temperature,
                target,
                range,
                TemperatureGradient.WhiteBounds);

        Assert.Equal(StatusColor.GreenColor, Color(target));
        Assert.Equal(StatusColor.White, Color(target - range));
        Assert.Equal(StatusColor.White, Color(target + range));
        Assert.Equal(new StatusColor(128, 255, 128), Color(target + range / 2));
        Assert.Equal(StatusColor.IceBlue, Color(target - range - 5));
        Assert.Equal(StatusColor.RedColor, Color(target + range + 5));
        Assert.Equal(StatusColor.IceBlue, Color(target - range - 50));
        Assert.Equal(StatusColor.RedColor, Color(target + range + 50));
    }

    [Fact]
    public void ClippingCountsVisibleCharactersAndResetsColorBeforeEllipsis()
    {
        var line = new StatusLine(new("12345", StatusColor.GreenColor), new("6789"));

        Assert.Equal("\u001b[38;2;0;255;0m123\u001b[0m…", line.Render(4));
        Assert.Equal("123…", line.Render(4, useColor: false));
        Assert.Equal("123456789", line.Render(9, useColor: false));
        Assert.Equal("…", line.Render(1));
        Assert.Equal(string.Empty, line.Render(0));
    }
}