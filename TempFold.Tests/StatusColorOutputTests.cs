// Copyright (c) 2026 smallketchup82 <ketchup@smkt.ca>. Licensed under the MIT License.
// See the LICENSE file in the repository root for full license text.

using Spectre.Console;
using TempFold.Logging;

namespace TempFold.Tests;

public sealed class StatusColorOutputTests
{
    [Theory]
    [InlineData(ColorSystem.Standard)]
    [InlineData(ColorSystem.EightBit)]
    [InlineData(ColorSystem.TrueColor)]
    public void StatusOutputKeepsExactRgbEvenWhenDetectionReportsALimitedPalette(ColorSystem detected)
    {
        using var output = new StringWriter();
        AnsiWriter writer = StatusConsole.CreateStatusWriter(
            output,
            new AnsiCapabilities { Ansi = true, ColorSystem = detected });
        // At 19°C, palette conversion used to replace this cyan tint with gray.
        var line = new StatusLine(new StatusSegment("19°C", StatusColor.ForTemperature(19, 20, 1)));

        line.WriteTo(writer, 100);

        Assert.Equal("\u001b[38;2;234;249;255m19°C\u001b[0m", output.ToString());
        Assert.Equal(ColorSystem.TrueColor, writer.Capabilities.ColorSystem);
    }

    [Fact]
    public void DisabledColorsAndAnsiRemainDisabled()
    {
        using var output = new StringWriter();
        AnsiWriter noColors = StatusConsole.CreateStatusWriter(
            output,
            new AnsiCapabilities { Ansi = true, ColorSystem = ColorSystem.NoColors });
        var line = new StatusLine(new StatusSegment("19°C", StatusColor.IceBlue));

        line.WriteTo(noColors, 100, flashStrength: 1);

        Assert.Equal(ColorSystem.NoColors, noColors.Capabilities.ColorSystem);
        Assert.Equal("19°C", output.ToString());

        output.GetStringBuilder().Clear();
        AnsiWriter noAnsi = StatusConsole.CreateStatusWriter(
            output,
            new AnsiCapabilities { Ansi = false, ColorSystem = ColorSystem.EightBit });

        line.WriteTo(noAnsi, 100);

        Assert.False(noAnsi.Capabilities.Ansi);
        Assert.Equal("19°C", output.ToString());
    }

    [Fact]
    public void ColdGradientIsSmoothAndBlueTintedAcrossReportedProblemRegion()
    {
        StatusColor previous = StatusColor.ForTemperature(16, 20, 1);
        for (int step = 1601; step <= 1900; step++)
        {
            StatusColor color = StatusColor.ForTemperature(step / 100.0, 20, 1);

            Assert.InRange(color.Red - previous.Red, 0, 1);
            Assert.InRange(color.Green - previous.Green, 0, 1);
            Assert.Equal(255, color.Blue);
            Assert.True(color.Red <= color.Green && color.Green < color.Blue);

            previous = color;
        }
    }
}