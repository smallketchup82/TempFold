// Copyright (c) 2026 smallketchup82 <ketchup@smkt.ca>. Licensed under the MIT License.
// See the LICENSE file in the repository root for full license text.

using TempFold.Control;
using TempFold.Models;
using TempFold.Tests.Support;

namespace TempFold.Tests;

[Trait("Category", "Integration")]
public sealed class ControllerPauseIntegrationTests
{
    [Fact]
    public async Task StartupPauseAllowsOneTimeManualCommandsAndFollowsExternalClientChanges()
    {
        await using var host = await FahServiceTestHost.CreateAsync(options => options with { StartPaused = true });
        TemperatureController controller = Controller(host);

        controller.ProcessReading(18);
        FahTestSession client = await host.StartServiceAsync();

        Assert.Equal(PauseReason.Manual, controller.GetSnapshot().PauseReason);
        await client.AssertNoCommandAsync();

        Assert.True(controller.SetFoldingManually(true));
        Assert.Equal("fold", await client.ReadCommandAsync());
        Assert.Equal(new FoldingStatus(false, false), controller.GetSnapshot().FoldingStatus);

        await client.ConfirmFoldingAndStartWorkAsync();
        await host.WaitForStatusAsync(new(true, true));
        await client.ConfirmPauseAsync(); // External stop must not reassert the manual start.
        await host.WaitForStatusAsync(new(false, false));

        await client.AssertNoCommandAsync();

        Assert.True(controller.SetFoldingManually(false)); // Explicitly send even when already stopped.
        Assert.Equal("pause", await client.ReadCommandAsync());

        await client.ConfirmFoldingAndStartWorkAsync(); // External start must not reassert the manual stop.
        await host.WaitForStatusAsync(new(true, true));
        controller.ProcessReading(30);

        await client.AssertNoCommandAsync();
        Assert.Equal(PauseReason.Manual, controller.GetSnapshot().PauseReason);
        Assert.Equal(new FoldingStatus(true, true), controller.GetSnapshot().FoldingStatus);

        controller.ToggleManualPause(); // Automatic control now stops the hot machine.

        Assert.Equal("pause", await client.ReadCommandAsync());
        Assert.False(controller.GetSnapshot().Paused);
        Assert.False(controller.SetFoldingManually(true));
    }

    [Fact]
    public async Task ResumingAfterManualStopRestartsWhenCool()
    {
        await using var host = await FahServiceTestHost.CreateAsync(options => options with { StartPaused = true });
        TemperatureController controller = Controller(host);

        controller.ProcessReading(22); // Resuming below the stop threshold also takes control in the middle band.
        FahTestSession client = await host.StartServiceAsync();

        controller.SetFoldingManually(false);

        Assert.Equal("pause", await client.ReadCommandAsync());

        controller.ToggleManualPause();

        Assert.Equal("fold", await client.ReadCommandAsync());

        await client.ConfirmFoldingAndStartWorkAsync();
        await host.WaitForStatusAsync(new(true, true));

        Assert.False(controller.GetSnapshot().Paused);
    }

    [Theory]
    [InlineData(true, 30, null)]
    [InlineData(false, 18, "fold")]
    public async Task ResumeCancelsPendingManualCommandBeforeReconnect(
        bool manualStart,
        double temperature,
        string? expected)
    {
        await using var host = await FahServiceTestHost.CreateAsync(options => options with { StartPaused = true });
        TemperatureController controller = Controller(host);
        FahTestSession first = await host.StartServiceAsync();

        first.Disconnect();
        await host.WaitForStatusAsync(null);
        controller.SetFoldingManually(manualStart);
        controller.ProcessReading(temperature);
        controller.ToggleManualPause();
        FahTestSession reconnected = await host.WaitForConnectionAsync();

        await host.WaitForStatusAsync(new(false, false));
        if (expected is null)
            await reconnected.AssertNoCommandAsync();
        else
        {
            Assert.Equal(expected, await reconnected.ReadCommandAsync());

            await reconnected.ConfirmFoldingAndStartWorkAsync();
            await host.WaitForStatusAsync(new(true, true));

            await reconnected.AssertNoCommandAsync();
        }

        Assert.False(controller.GetSnapshot().Paused);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InhibitorAllowsExternalFoldingAfterPauseConfirmationAndPreservesReportedState(bool manual)
    {
        await using var host = await FahServiceTestHost.CreateAsync();
        FahTestSession client = await host.StartServiceAsync();
        bool gameMode = false;
        TemperatureController controller = Controller(host, () => new(gameMode, null));

        controller.ProcessReading(18);

        Assert.Equal("fold", await client.ReadCommandAsync());

        await client.ConfirmFoldingAndStartWorkAsync();
        await host.WaitForStatusAsync(new(true, true));

        if (manual)
            controller.Pause(true, manual: true);
        else
        {
            gameMode = true;
            controller.RefreshSchedule();
        }

        Assert.Equal("pause", await client.ReadCommandAsync());
        // Sending a command never optimistically changes the client-reported state.
        Assert.Equal(new FoldingStatus(true, true), controller.GetSnapshot().FoldingStatus);

        await client.ConfirmPauseAsync();
        await host.WaitForStatusAsync(new(false, false));

        await client.ConfirmFoldingAndStartWorkAsync();
        await host.WaitForStatusAsync(new(true, true));
        controller.ProcessReading(18);
        controller.ProcessReading(30);
        controller.SetTargetOverride(40);
        controller.RefreshSchedule();
        host.Service.SetFolding(true); // The transport also enforces the start inhibitor.
        host.Service.SetFolding(false);

        await client.AssertNoCommandAsync();
        Assert.Equal(manual ? PauseReason.Manual : PauseReason.GameMode, controller.GetSnapshot().PauseReason);
        Assert.Equal(new FoldingStatus(true, true), controller.GetSnapshot().FoldingStatus);
    }

    [Fact]
    public async Task ManualPauseCancelsAnUnconfirmedStartAndPreventsReplayAfterReconnect()
    {
        await using var host = await FahServiceTestHost.CreateAsync();
        FahTestSession first = await host.StartServiceAsync();
        TemperatureController controller = Controller(host);

        controller.ProcessReading(18);

        Assert.Equal("fold", await first.ReadCommandAsync());

        // No folding confirmation arrived; the old start must still be replaced.
        controller.Pause(true, manual: true);

        Assert.Equal("pause", await first.ReadCommandAsync());

        first.Disconnect();
        await host.WaitForStatusAsync(null);
        FahTestSession reconnected = await host.WaitForConnectionAsync();

        await host.WaitForStatusAsync(new(false, false));
        controller.ProcessReading(18);

        await reconnected.AssertNoCommandAsync();
        Assert.Equal(PauseReason.Manual, controller.GetSnapshot().PauseReason);

        await reconnected.ConfirmFoldingAndStartWorkAsync();
        await host.WaitForStatusAsync(new(true, true));

        await reconnected.AssertNoCommandAsync();
    }

    [Theory]
    [InlineData(22, "fold")]
    [InlineData(25, null)]
    public async Task ManualResumeUsesLatestTemperatureAndDoesNotRestartWhenHot(double temperature, string? command)
    {
        await using var host = await FahServiceTestHost.CreateAsync();
        FahTestSession client = await host.StartServiceAsync();
        TemperatureController controller = Controller(host);

        controller.Pause(true, manual: true);
        controller.ProcessReading(temperature);

        await client.AssertNoCommandAsync(); // FAH already reported paused at connection.

        controller.Pause(false, manual: true);
        if (command is null)
            await client.AssertNoCommandAsync();
        else
            Assert.Equal(command, await client.ReadCommandAsync());

        Assert.False(controller.GetSnapshot().Paused);
    }

    [Fact]
    public async Task HotReadingCancelsAnUnconfirmedStartBeforeReconnect()
    {
        await using var host = await FahServiceTestHost.CreateAsync();
        FahTestSession first = await host.StartServiceAsync();
        TemperatureController controller = Controller(host);

        controller.ProcessReading(18);

        Assert.Equal("fold", await first.ReadCommandAsync());

        controller.ProcessReading(25);

        Assert.Equal("pause", await first.ReadCommandAsync());

        first.Disconnect();
        await host.WaitForStatusAsync(null);
        FahTestSession reconnected = await host.WaitForConnectionAsync();

        await host.WaitForStatusAsync(new(false, false));

        await reconnected.AssertNoCommandAsync();
    }

    [Fact]
    public async Task ExternalStartWithoutStopConfirmationDoesNotTriggerAPauseRetryOrConfirmationReconnect()
    {
        await using var host = await FahServiceTestHost.CreateAsync();
        FahTestSession client = await host.StartServiceAsync();
        TemperatureController controller = Controller(host);

        controller.ProcessReading(18);

        Assert.Equal("fold", await client.ReadCommandAsync());

        await client.ConfirmFoldingAndStartWorkAsync();
        await host.WaitForStatusAsync(new(true, true));
        controller.Pause(true, manual: true);

        Assert.Equal("pause", await client.ReadCommandAsync());

        // No paused update is ever delivered; an external start wins in FAH.
        await client.ConfirmFoldingAndStartWorkAsync();
        await host.WaitForStatusAsync(new(true, true));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            host.WaitForConnectionAsync(TimeSpan.FromSeconds(32)));
        Assert.False(client.IsDisconnected);
        Assert.Equal(new FoldingStatus(true, true), controller.GetSnapshot().FoldingStatus);
        Assert.Equal(PauseReason.Manual, controller.GetSnapshot().PauseReason);
        await client.AssertNoCommandAsync();
    }

    private static TemperatureController Controller(FahServiceTestHost host, Func<GameModeStatus>? gameMode = null) =>
        new(
            host.Options,
            gameMode ?? (() => new(false, null)),
            host.Service.SetFolding,
            host.Service.GetStatus,
            TimeProvider.System,
            host.Service.SetPaused,
            host.Service.SetFoldingManually);
}