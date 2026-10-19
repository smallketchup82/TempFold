// Copyright (c) 2026 smallketchup82 <ketchup@smkt.ca>. Licensed under the MIT License.
// See the LICENSE file in the repository root for full license text.

using Spectre.Console;

namespace TempFold.Logging;

internal enum TemperatureGradient { WhiteBounds, Direct }

internal readonly record struct StatusColor(int Red, int Green, int Blue)
{
    public static readonly StatusColor GreenColor = new(0, 255, 0);
    public static readonly StatusColor White = new(255, 255, 255);
    public static readonly StatusColor RedColor = new(255, 0, 0);
    public static readonly StatusColor IceBlue = new(128, 220, 255);
    public static readonly StatusColor Muted = new(160, 160, 160);
    public static readonly StatusColor Black = new(0, 0, 0);
    private const double FullTrendColorChange = 3;

    public static StatusColor ForTrend(double change) => Blend(White,
        change < 0 ? IceBlue : RedColor,
        Math.Clamp(Math.Abs(change) / FullTrendColorChange, 0, 1));

    public static StatusColor ForTemperature(double temperature, double target, double range,
        TemperatureGradient gradient = TemperatureGradient.Direct)
    {
        double distance = Math.Abs(temperature - target);
        if (gradient == TemperatureGradient.Direct)
        {
            StatusColor baseline = Blend(White, temperature < target ? IceBlue : RedColor,
                Math.Clamp(distance / (range + 5), 0, 1));
            // A small green overlay fades into the underlying gradient without a jump.
            double greenAmount = range > 0
                ? Math.Clamp(1 - distance / (range * 0.25), 0, 1)
                : distance == 0 ? 1 : 0;
            return Blend(baseline, GreenColor, greenAmount);
        }

        if (distance <= range)
            return Blend(GreenColor, White, range == 0 ? 0 : distance / range);

        return Blend(White, temperature < target ? IceBlue : RedColor,
            Math.Clamp((distance - range) / 5, 0, 1));
    }

    internal static StatusColor Blend(StatusColor from, StatusColor to, double amount) => new(
        (int)Math.Round(from.Red + (to.Red - from.Red) * amount),
        (int)Math.Round(from.Green + (to.Green - from.Green) * amount),
        (int)Math.Round(from.Blue + (to.Blue - from.Blue) * amount));
}

internal readonly record struct StatusSegment(string Text, StatusColor? Color = null);

internal sealed class StatusLine(params StatusSegment[] segments)
{
    internal StatusLine Prepend(params StatusSegment[] prefix) => new([.. prefix, .. segments]);
    internal StatusLine Append(params StatusSegment[] suffix) => new([.. segments, .. suffix]);

    // Deterministic capture for tests; production reuses its capability-aware writer.
    internal string Render(int maxLength, bool useColor = true, double flashStrength = 0)
    {
        using var output = new StringWriter();
        var writer = new AnsiWriter(output, new AnsiCapabilities
        {
            Ansi = useColor,
            ColorSystem = useColor ? ColorSystem.TrueColor : ColorSystem.NoColors
        });
        WriteTo(writer, maxLength, flashStrength);
        return output.ToString();
    }

    internal void WriteTo(AnsiWriter writer, int maxLength, double flashStrength = 0)
    {
        if (maxLength <= 0)
            return;

        double flash = writer.Capabilities.ColorSystem == ColorSystem.NoColors
            ? 0 : Math.Clamp(flashStrength, 0, 1);
        StatusColor? background = flash > 0 ? StatusColor.Blend(StatusColor.Black, StatusColor.White, flash) : null;

        void Write(string text, StatusColor? foreground)
        {
            // At full strength both text and background are white; fading reveals the row.
            if (flash > 0)
                foreground = StatusColor.Blend(foreground ?? StatusColor.White, StatusColor.White, flash);

            Color Convert(StatusColor? color) => color is { } rgb
                ? new Color((byte)rgb.Red, (byte)rgb.Green, (byte)rgb.Blue) : Color.Default;
            if (foreground is not null || background is not null)
                writer.Write(text, new Style(foreground: Convert(foreground), background: Convert(background)));
            else
                writer.Write(text);
        }

        int length = segments.Sum(segment => segment.Text.Length);
        bool clipped = length > maxLength;
        int remaining = clipped ? maxLength - 1 : maxLength;
        foreach (StatusSegment segment in segments)
        {
            if (remaining <= 0)
                break;
            string text = segment.Text[..Math.Min(segment.Text.Length, remaining)];
            Write(text, segment.Color);
            remaining -= text.Length;
        }
        if (clipped)
            Write("…", null);
        else if (background is not null && remaining > 0)
            Write(new string(' ', remaining), null);
    }
}
