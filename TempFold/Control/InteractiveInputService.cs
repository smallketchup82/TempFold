// Copyright (c) 2026 smallketchup82 <ketchup@smkt.ca>. Licensed under the MIT License.
// See the LICENSE file in the repository root for full license text.

using System.Globalization;
using TempFold.Logging;

namespace TempFold.Control;

// Keeps keyboard editing separate from terminal rendering and sensor ingestion.
internal sealed class InteractiveTemperatureInput(TemperatureController controller, Action? quit = null)
{
    private bool? editingTarget;
    private bool showingHelp;
    private string buffer = "";
    private string? error;
    internal string? Prompt => showingHelp ? "Press Enter or any key to continue..." : editingTarget is { } target
        ? $"{(target ? "Temperature" : "Range")}: {buffer}{error}"
        : null;
    internal string[] Instructions => showingHelp
        ? ["TempFold interactive actions:",
            "  t  Set a temporary target temperature (°C).",
            "  r  Set a temporary range around the target (±°C).",
            "  d  Clear both overrides and use the current schedule or CLI defaults.",
            "  p  Toggle manual pause; block TempFold starts while paused.",
            "  f  While paused, manually request folding to start.",
            "  s  While paused, manually request folding to stop.",
            "  q  Quit TempFold gracefully.",
            "  ?  Show this help.",
            "  Enter  Apply a value; Esc cancels; Backspace edits.",
            "  Ctrl+C  Shut down TempFold.",
            "Overrides stay active across schedule changes until cleared or restarted."]
        : ["Enter a value in °C, then press Enter to set it. Esc cancels; Backspace edits."];

    internal void HandleKey(ConsoleKeyInfo key)
    {
        if (showingHelp)
        {
            showingHelp = false;
            return;
        }
        if (editingTarget is null)
        {
            if (key.KeyChar == '?') { controller.LogInput("help"); showingHelp = true; }
            switch (key.Key)
            {
                case ConsoleKey.T: editingTarget = true; break;
                case ConsoleKey.R: editingTarget = false; break;
                case ConsoleKey.D: controller.ClearOverrides(); break;
                case ConsoleKey.P: controller.ToggleManualPause(); break;
                case ConsoleKey.F: controller.SetFoldingManually(true); break;
                case ConsoleKey.S: controller.SetFoldingManually(false); break;
                case ConsoleKey.Q: controller.LogInput("quit"); quit?.Invoke(); break;
            }
            buffer = "";
            error = null;
            return;
        }
        if (key.Key == ConsoleKey.Escape)
        {
            controller.LogInput("cancelEdit", editingTarget == true ? "target" : "range");
            editingTarget = null;
        }
        else if (key.Key == ConsoleKey.Backspace)
        {
            if (buffer.Length > 0) buffer = buffer[..^1];
            error = null;
        }
        else if (key.Key == ConsoleKey.Enter)
        {
            bool valid = double.TryParse(buffer, NumberStyles.Float, CultureInfo.CurrentCulture, out double value)
                || double.TryParse(buffer, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
            if (!valid || !double.IsFinite(value) || (editingTarget == false && value <= 0))
            {
                controller.LogInput(editingTarget == true ? "invalidTarget" : "invalidRange", buffer);
                error = editingTarget == true ? " | Use a finite number" : " | Range must be greater than 0";
                return;
            }
            if (editingTarget == true) controller.SetTargetOverride(value);
            else controller.SetRangeOverride(value);
            editingTarget = null;
        }
        else if (buffer.Length < 32 && (char.IsAsciiDigit(key.KeyChar) || key.KeyChar is '+' or '-' or '.' or ',' or 'e' or 'E'))
        {
            buffer += key.KeyChar;
            error = null;
        }
    }
}

internal sealed class InteractiveInputService(TemperatureController controller, StatusConsole console,
    IHostApplicationLifetime lifetime) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!console.InputEnabled) return;
        var input = new InteractiveTemperatureInput(controller, lifetime.StopApplication);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                while (!stoppingToken.IsCancellationRequested && Console.KeyAvailable)
                {
                    string? previousPrompt = input.Prompt;
                    input.HandleKey(Console.ReadKey(intercept: true));
                    if (previousPrompt is null && input.Prompt is { } prompt)
                        console.ShowInteraction(input.Instructions, prompt);
                    else if (previousPrompt != input.Prompt)
                        console.SetInputPrompt(input.Prompt);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            // A terminal may detach after startup; the controller can keep running.
        }
        finally { console.SetInputPrompt(null); }
    }
}
