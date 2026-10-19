// Copyright (c) 2026 smallketchup82 <ketchup@smkt.ca>. Licensed under the MIT License.
// See the LICENSE file in the repository root for full license text.

using TempFold.Control;

namespace TempFold.Tests;

public sealed class InteractiveInputTests
{
    private readonly TemperatureController controller;
    private readonly InteractiveTemperatureInput input;
    private bool quitRequested;
    private readonly List<bool> commands = [];

    public InteractiveInputTests()
    {
        controller = new(
            new ControllerOptions(
                20,
                3,
                "unused",
                115200,
                new Uri("ws://localhost/socket")),
            () => new(false, null),
            commands.Add,
            () => null,
            TimeProvider.System);
        input = new(controller, () => quitRequested = true);
    }

    [Fact]
    public void HelpListsAllActionsAndAnyKeyDismissesWithoutExecutingAnAction()
    {
        Press(ConsoleKey.Oem2, '?');

        Assert.Contains("any key", input.Prompt);

        foreach (string action in new[]
                 {
                     "t  ",
                     "r  ",
                     "d  ",
                     "p  ",
                     "f  ",
                     "s  ",
                     "q  ",
                     "?  ",
                     "Enter",
                     "Esc",
                     "Backspace",
                     "Ctrl+C"
                 })
            Assert.Contains(action, string.Join('\n', input.Instructions));
        Press(ConsoleKey.T, 't');

        Assert.Null(input.Prompt);
        Assert.False(controller.GetSnapshot().Settings.TargetOverridden);
    }

    [Fact]
    public void ManualStartAndStopOnlyActWhilePausedAndResumeRestoresAutomaticControl()
    {
        Press(ConsoleKey.F, 'f');
        Press(ConsoleKey.S, 's');

        Assert.Empty(commands);

        Press(ConsoleKey.P, 'p');

        Assert.Equal(new[] { false }, commands);

        commands.Clear();
        Press(ConsoleKey.F, 'f');
        Press(ConsoleKey.S, 's');

        Assert.Equal(new[] { true, false }, commands);
        Assert.Equal(Models.PauseReason.Manual, controller.GetSnapshot().PauseReason);

        controller.ProcessReading(18);

        Assert.Equal(2, commands.Count);

        Press(ConsoleKey.P, 'p');

        Assert.False(controller.GetSnapshot().Paused);
        Assert.Equal(new[] { true, false, true }, commands);
    }

    [Fact]
    public void PManuallyPausesAndResumesAndQRequestsGracefulShutdown()
    {
        Press(ConsoleKey.P, 'p');

        Assert.Equal(Models.PauseReason.Manual, controller.GetSnapshot().PauseReason);
        Assert.Null(input.Prompt);

        Press(ConsoleKey.P, 'p');

        Assert.False(controller.GetSnapshot().Paused);

        Press(ConsoleKey.Q, 'q');

        Assert.True(quitRequested);
    }

    [Fact]
    public void QuitKeyDismissesHelpAndDoesNotInterruptAValueEdit()
    {
        Press(ConsoleKey.Oem2, '?');
        Press(ConsoleKey.Q, 'q');

        Assert.False(quitRequested);
        Assert.Null(input.Prompt);

        Press(ConsoleKey.T, 't');
        Press(ConsoleKey.Q, 'q');

        Assert.False(quitRequested);
        Assert.Equal("Temperature: ", input.Prompt);
    }

    [Fact]
    public void EditsAreAppliedOnlyOnEnterAndClearedTogetherByD()
    {
        Press(ConsoleKey.T);

        Assert.Equal("Temperature: ", input.Prompt);

        Type("23.5");

        Assert.Equal(20, controller.GetSnapshot().Settings.Target);

        Press(ConsoleKey.Enter);

        Assert.Null(input.Prompt);
        Assert.Equal(23.5, controller.GetSnapshot().Settings.Target);
        Assert.True(controller.GetSnapshot().Settings.TargetOverridden);

        Press(ConsoleKey.R);

        Assert.Equal("Range: ", input.Prompt);

        Type("1.5");
        Press(ConsoleKey.Enter);

        Assert.True(controller.GetSnapshot().Settings.RangeOverridden);

        Press(ConsoleKey.D);

        Assert.Equal(20, controller.GetSnapshot().Settings.Target);
        Assert.Equal(3, controller.GetSnapshot().Settings.Range);
        Assert.False(controller.GetSnapshot().Settings.TargetOverridden);
        Assert.False(controller.GetSnapshot().Settings.RangeOverridden);
    }

    [Fact]
    public void InvalidValuesStayInThePromptAndCanBeCorrectedOrCancelled()
    {
        Press(ConsoleKey.R);
        Type("0");
        Press(ConsoleKey.Enter);

        Assert.Contains("greater than 0", input.Prompt);
        Assert.False(controller.GetSnapshot().Settings.RangeOverridden);

        Press(ConsoleKey.Backspace);
        Type("2");
        Press(ConsoleKey.Enter);

        Assert.Equal(2, controller.GetSnapshot().Settings.Range);

        Press(ConsoleKey.T);
        Type("1e999");
        Press(ConsoleKey.Enter);

        Assert.Contains("finite", input.Prompt);

        Press(ConsoleKey.Escape);

        Assert.Null(input.Prompt);
        Assert.False(controller.GetSnapshot().Settings.TargetOverridden);
    }

    private void Press(ConsoleKey key, char character = '\0') =>
        input.HandleKey(
            new(
                character,
                key,
                false,
                false,
                false));

    private void Type(string text)
    {
        foreach (char character in text)
            Press(ConsoleKey.NoName, character);
    }
}