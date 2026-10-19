// Copyright (c) 2026 smallketchup82 <ketchup@smkt.ca>. Licensed under the MIT License.
// See the LICENSE file in the repository root for full license text.

using System.Text.RegularExpressions;
using TempFold.Logging;

namespace TempFold.Tests;

public sealed class SensorFlashTests
{
    [Fact]
    public void FlashFadesOverThreeQuartersOfASecondAndCanRetrigger()
    {
        var clock = new Clock();
        var flash = new SensorFlash(clock);

        Assert.Equal(0, flash.Strength);

        flash.Restart();

        Assert.Equal(1, flash.Strength);

        clock.Advance(375);

        Assert.Equal(0.5, flash.Strength, precision: 12);

        flash.Restart();

        Assert.Equal(1, flash.Strength);

        clock.Advance(750);

        Assert.Equal(0, flash.Strength);

        clock.Advance(1000);

        Assert.Equal(0, flash.Strength);

        flash.Restart();
        flash.Reset();

        Assert.Equal(0, flash.Strength);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(150, 0.8)]
    [InlineData(375, 0.5)]
    [InlineData(600, 0.2)]
    [InlineData(750, 0)]
    [InlineData(1000, 0)]
    public void FlashRevealsTheRowLinearly(long elapsedMilliseconds, double expectedStrength)
    {
        var clock = new Clock();
        var flash = new SensorFlash(clock);

        flash.Restart();
        clock.Advance(elapsedMilliseconds);

        Assert.Equal(expectedStrength, flash.Strength, precision: 12);
    }

    [Fact]
    public void FullFlashWhitesOutTextAndPadsTheWholeUsableRow()
    {
        var line = new StatusLine(new("abc", StatusColor.GreenColor), new(" end"));
        string rendered = line.Render(10, flashStrength: 1);

        Assert.Contains("38;2;255;255;255", rendered);
        Assert.Contains("48;2;255;255;255", rendered);
        Assert.DoesNotContain("38;2;0;255;0", rendered);
        Assert.Equal("abc end   ", StripStyles(rendered));
        Assert.EndsWith("\u001b[0m", rendered);
    }

    [Fact]
    public void FadingRevealsColorsAndRemovesBackgroundAtCompletion()
    {
        var line = new StatusLine(new StatusSegment("abc", StatusColor.GreenColor));
        string fading = line.Render(10, flashStrength: 0.5);

        Assert.Contains("38;2;128;255;128", fading);
        Assert.Contains("48;2;128;128;128", fading);

        string complete = line.Render(10, flashStrength: 0);

        Assert.DoesNotContain("48;", complete);
        Assert.Equal("abc", StripStyles(complete));
        Assert.Contains("38;2;0;255;0", complete);
        Assert.Equal("abc", line.Render(10, useColor: false, flashStrength: 1));
        Assert.Equal("a…", StripStyles(line.Render(2, flashStrength: 1)));
    }

    private static string StripStyles(string text) => Regex.Replace(text, "\u001b\\[[0-9;]*m", "");

    private sealed class Clock : TimeProvider
    {
        private long timestamp;
        public override long TimestampFrequency => 1000;

        public override long GetTimestamp() => timestamp;

        public void Advance(long milliseconds) => timestamp += milliseconds;
    }
}