// Copyright (c) 2026 smallketchup82 <ketchup@smkt.ca>. Licensed under the MIT License.
// See the LICENSE file in the repository root for full license text.

using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Sinks.SystemConsole.Themes;
using Spectre.Console;
using System.Text.Json;
using TempFold.Api;
using TempFold.Models;

namespace TempFold.Logging;

// Owns all terminal writes, including Serilog's normal console renderer.
internal sealed class StatusConsole : ILogEventSink, IDisposable
{
    private readonly object syncRoot = new();
    private readonly Logger consoleLogger;
    private readonly AnsiWriter statusWriter;
    private readonly SensorFlash flash;
    private readonly Timer animationTimer;
    private bool disposed;
    private readonly bool interactive = !Console.IsOutputRedirected
                                        && Environment.GetEnvironmentVariable("TERM") != "dumb";
    private StatusLine? status;
    private string? inputPrompt;
    private string[] interactionInstructions = [];
    private int drawnRows;
    private readonly StatusOutputOptions options;
    private readonly TextWriter output;
    public bool EffectsEnabled => interactive && options.Effects && statusWriter.Capabilities.ColorSystem != ColorSystem.NoColors;
    public bool InputEnabled => interactive && !Console.IsInputRedirected;

    public StatusConsole() : this(new StatusOutputOptions()) { }

    public StatusConsole(StatusOutputOptions options, TextWriter? output = null)
    {
        this.options = options;
        this.output = output ?? Console.Out;
        flash = new SensorFlash(TimeProvider.System);
        statusWriter = CreateStatusWriter(this.output, colors: options.Colors && !options.Json);
        interactive = interactive && options.Interactive && !options.Json && statusWriter.Capabilities.Ansi;
        animationTimer = new Timer(_ => Animate(), null, Timeout.Infinite, Timeout.Infinite);
        consoleLogger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Console(theme: options.Colors ? AnsiConsoleTheme.Code : ConsoleTheme.None,
                standardErrorFromLevel: options.Json ? LogEventLevel.Verbose : null, syncRoot: syncRoot)
            .CreateLogger();
    }

    internal static AnsiWriter CreateStatusWriter(TextWriter output, AnsiCapabilities? capabilities = null, bool colors = true)
    {
        capabilities ??= AnsiCapabilities.Create(output);
        // Palette matching causes hue/brightness jumps in the near-white gradients.
        // Modern terminals often support RGB even when TERM only advertises 256 colors.
        if (!colors)
            capabilities.ColorSystem = ColorSystem.NoColors;
        else if (capabilities.Ansi && capabilities.ColorSystem != ColorSystem.NoColors)
            capabilities.ColorSystem = ColorSystem.TrueColor;
        return new AnsiWriter(output, capabilities);
    }

    public void Emit(LogEvent logEvent)
    {
        lock (syncRoot)
        {
            if (disposed)
                return;
            ClearLine();
            try
            {
                consoleLogger.Write(logEvent);
            }
            finally
            {
                DrawStatus();
            }
        }
    }

    public void Update(string value) => Update(new StatusLine(new StatusSegment(value)));

    public void SetInputPrompt(string? prompt)
    {
        lock (syncRoot)
        {
            if (disposed || !interactive) return;
            ClearLine();
            inputPrompt = prompt;
            if (prompt is null) interactionInstructions = [];
            DrawStatus();
        }
    }

    public void ShowInteraction(IEnumerable<string> instructions, string prompt)
    {
        lock (syncRoot)
        {
            if (disposed || !interactive) return;
            ClearLine();
            inputPrompt = prompt;
            interactionInstructions = instructions.ToArray();
            DrawStatus();
        }
    }

    public void Update(StatusLine value, bool sensorUpdate = false)
    {
        lock (syncRoot)
        {
            // Redirected output is a log stream, not a live terminal.
            if (disposed || options.Json)
                return;
            if (!interactive)
            {
                if (!options.Interactive)
                {
                    value.WriteTo(statusWriter, int.MaxValue);
                    statusWriter.WriteLine();
                    output.Flush();
                }
                return;
            }

            ClearLine();
            status = value;
            bool startFlash = sensorUpdate && EffectsEnabled;
            if (startFlash)
            {
                flash.Restart();
                animationTimer.Change(50, 50);
            }
            DrawStatus(startFlash ? 1 : null);
        }
    }

    public void WriteReport(StatusReport report)
    {
        lock (syncRoot)
        {
            if (disposed || !options.Json)
                return;
            output.WriteLine(JsonSerializer.Serialize(report, SensorJsonContext.Default.StatusReport));
            output.Flush();
        }
    }

    public void Clear()
    {
        lock (syncRoot)
        {
            ClearLine();
            status = null;
            inputPrompt = null;
            interactionInstructions = [];
            flash.Reset();
            if (!disposed)
                animationTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }
    }

    private void Animate()
    {
        lock (syncRoot)
        {
            if (disposed || status is null)
                return;
            ClearLine();
            DrawStatus();
            if (flash.Strength <= 0)
                animationTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }
    }

    private void ClearLine()
    {
        if (!interactive || drawnRows == 0) return;
        // The cursor rests on the last row of the live area. Clear upward so logs
        // are written above the entire help block or input prompt before redrawing.
        statusWriter.CursorHorizontalAbsolute(1).EraseInLine(2);
        for (int row = 1; row < drawnRows; row++)
            statusWriter.CursorUp(1).CursorHorizontalAbsolute(1).EraseInLine(2);
        drawnRows = 0;
    }

    private void DrawStatus(double? flashStrength = null)
    {
        if (!interactive || (status is null && inputPrompt is null))
            return;

        // Leave the final column free to prevent wrapping onto another row.
        int width = Console.WindowWidth;
        if (width <= 1)
            return;
        if (inputPrompt is not null)
        {
            int height = Console.WindowHeight;
            int instructionRows = height > 0 ? Math.Max(0, height - 2) : interactionInstructions.Length;
            foreach (string instruction in interactionInstructions.Take(instructionRows))
            {
                new StatusLine(new StatusSegment(instruction)).WriteTo(statusWriter, width - 1);
                statusWriter.WriteLine();
                drawnRows++;
            }
            new StatusLine(new StatusSegment(inputPrompt)).WriteTo(statusWriter, width - 1);
        }
        else
            status!.WriteTo(statusWriter, width - 1, flashStrength ?? flash.Strength);
        drawnRows++;
        output.Flush();
    }

    public void Dispose()
    {
        lock (syncRoot)
        {
            if (disposed)
                return;
            Clear();
            disposed = true;
            animationTimer.Dispose();
            consoleLogger.Dispose();
        }
    }
}
