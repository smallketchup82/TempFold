// Copyright (c) 2026 smallketchup82 <ketchup@smkt.ca>. Licensed under the MIT License.
// See the LICENSE file in the repository root for full license text.

using TempFold.Models;
using TempFold.Tests.Support;

namespace TempFold.Tests;

[Trait("Category", "Integration")]
public sealed class FahServiceIntegrationTests : IAsyncLifetime
{
    private FahServiceTestHost host = null!;

    public async Task InitializeAsync()
    {
        host = await FahServiceTestHost.CreateAsync();
    }

    public Task DisposeAsync() => host.DisposeAsync().AsTask();

    [Fact]
    public async Task StartupReadsFragmentedSnapshotWithoutSendingACommand()
    {
        FahTestSession client = await host.StartServiceAsync();

        Assert.Equal(new FoldingStatus(false, false), host.Service.GetStatus());
        await client.AssertNoCommandAsync();
    }

    [Fact]
    public async Task StartupReplaysFoldingIntentSetBeforeConnecting()
    {
        host.Service.SetFolding(true);

        FahTestSession client = await host.StartServiceAsync();

        Assert.Equal("fold", await client.ReadCommandAsync());
    }

    [Fact]
    public async Task RepeatedIntentDoesNotResendAnUnconfirmedCommand()
    {
        FahTestSession client = await host.StartServiceAsync();

        host.Service.SetFolding(true);

        Assert.Equal("fold", await client.ReadCommandAsync());

        for (int reading = 0; reading < 100; reading++)
        {
            host.Service.SetFolding(true);
        }

        await client.AssertNoCommandAsync();
    }

    [Fact]
    public async Task OppositeIntentOverridesPendingCommandEvenWhenSnapshotAlreadyMatches()
    {
        FahTestSession client = await host.StartServiceAsync();

        host.Service.SetFolding(true);

        Assert.Equal("fold", await client.ReadCommandAsync());

        // The client has not confirmed folding, so the cached snapshot still says paused.
        host.Service.SetFolding(false);

        Assert.Equal("pause", await client.ReadCommandAsync());

        host.Service.SetFolding(true);

        Assert.Equal("fold", await client.ReadCommandAsync());
    }

    [Fact]
    public async Task ClientUpdatesPublishWorkingStatusAndAllowNewIntent()
    {
        FahTestSession client = await host.StartServiceAsync();

        host.Service.SetFolding(true);

        Assert.Equal("fold", await client.ReadCommandAsync());

        await client.ConfirmFoldingAndStartWorkAsync();
        await host.WaitForStatusAsync(new FoldingStatus(true, true));

        host.Service.SetFolding(false);

        Assert.Equal("pause", await client.ReadCommandAsync());
    }

    [Fact]
    public async Task DisconnectClearsStatusAndReconnectReplaysOnlyLatestOfflineIntent()
    {
        FahTestSession firstClient = await host.StartServiceAsync();

        firstClient.Disconnect();
        await host.WaitForStatusAsync(null);

        Assert.False(host.Service.IsConnected);

        host.Service.SetFolding(false);
        host.Service.SetFolding(true);

        FahTestSession reconnectedClient = await host.WaitForConnectionAsync();

        Assert.Equal("fold", await reconnectedClient.ReadCommandAsync());
        await reconnectedClient.AssertNoCommandAsync();
    }

    [Fact]
    public async Task ExternalPauseAfterConfirmationReappliesDesiredFoldingState()
    {
        FahTestSession client = await host.StartServiceAsync();

        host.Service.SetFolding(true);

        Assert.Equal("fold", await client.ReadCommandAsync());

        await client.ConfirmFoldingAndStartWorkAsync();
        await host.WaitForStatusAsync(new FoldingStatus(true, true));

        await client.SetGroupPausedAsync("gpu", paused: true);

        Assert.Equal("fold", await client.ReadCommandAsync());
    }

    [Fact]
    public async Task MalformedMessageDisconnectsAndPreservesIntentForReconnect()
    {
        FahTestSession client = await host.StartServiceAsync();

        host.Service.SetFolding(true);

        Assert.Equal("fold", await client.ReadCommandAsync());

        await client.SendTextAsync("{invalid");
        await host.WaitForStatusAsync(null);
        FahTestSession reconnectedClient = await host.WaitForConnectionAsync();

        Assert.Equal("fold", await reconnectedClient.ReadCommandAsync());
    }

    [Fact]
    public async Task UnconfirmedCommandTimesOutAndIsRetriedOnANewConnection()
    {
        FahTestSession client = await host.StartServiceAsync();

        host.Service.SetFolding(true);

        Assert.Equal("fold", await client.ReadCommandAsync());

        // Exercise the real 30-second deadline without waiting a fixed amount of time.
        FahTestSession reconnectedClient = await host.WaitForConnectionAsync(TimeSpan.FromSeconds(40));

        Assert.True(client.IsDisconnected);
        Assert.False(client.HasUnreadCommands);
        Assert.Equal("fold", await reconnectedClient.ReadCommandAsync());
    }

    [Fact]
    public async Task ShutdownClosesActiveConnectionAndClearsStatus()
    {
        FahTestSession client = await host.StartServiceAsync();

        await host.StopServiceAsync();
        await client.WaitForDisconnectAsync();

        Assert.False(host.Service.IsConnected);
        Assert.Null(host.Service.GetStatus());

        FahTestSession shutdownClient = await host.WaitForConnectionAsync();

        Assert.Equal("pause", await shutdownClient.ReadCommandAsync());
    }

    [Fact]
    public async Task DisablingPauseOnExitDoesNotSendPauseOrReconnect()
    {
        await using var noPauseHost = await FahServiceTestHost.CreateAsync(o => o with { PauseFahOnExit = false });
        FahTestSession client = await noPauseHost.StartServiceAsync();

        noPauseHost.Service.SetFolding(true);

        Assert.Equal("fold", await client.ReadCommandAsync());

        await noPauseHost.StopServiceAsync();
        await client.WaitForDisconnectAsync();

        Assert.False(client.HasUnreadCommands);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            noPauseHost.WaitForConnectionAsync(TimeSpan.FromMilliseconds(250)));
    }
}