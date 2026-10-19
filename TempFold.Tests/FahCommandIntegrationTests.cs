// Copyright (c) 2026 smallketchup82 <ketchup@smkt.ca>. Licensed under the MIT License.
// See the LICENSE file in the repository root for full license text.

using TempFold.CommandLine;
using TempFold.Tests.Support;

namespace TempFold.Tests;

[Trait("Category", "Integration")]
public sealed class FahCommandIntegrationTests
{
    [Theory]
    [InlineData("start", "fold")]
    [InlineData("stop", "pause")]
    public async Task DirectCommandsSendExactlyOnceAndDisconnectWithoutShutdownPause(string action, string expected)
    {
        await using var host = await FahServiceTestHost.CreateAsync(options => options with { PauseFahOnExit = false });
        var output = new StringWriter();
        var error = new StringWriter();
        Task<int> command = FahCommand.InvokeAsync(
            [action, "--fah-endpoint", host.Options.FahEndpoint.ToString()],
            defaultConfigPath: Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"),
            output: output,
            error: error);
        FahTestSession client = await host.WaitForConnectionAsync();

        Assert.Equal(expected, await client.ReadCommandAsync());
        Assert.Equal(0, await command);

        await client.WaitForDisconnectAsync();

        Assert.False(client.HasUnreadCommands);
        Assert.Contains($"Requested Folding@home {action}", output.ToString());
        Assert.Equal("", error.ToString());
        Assert.Null(host.Service.GetStatus()); // The thermostat service was never started.
    }

    [Fact]
    public async Task StatusReadsTheSnapshotWithoutSendingCommands()
    {
        await using var host = await FahServiceTestHost.CreateAsync(options => options with { PauseFahOnExit = false });
        var output = new StringWriter();

        Assert.Equal(
            0,
            await FahCommand.InvokeAsync(
                ["status", "--fah-endpoint", host.Options.FahEndpoint.ToString()],
                defaultConfigPath: Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"),
                output: output));

        FahTestSession client = await host.WaitForConnectionAsync();

        await client.WaitForDisconnectAsync();

        Assert.False(client.HasUnreadCommands);
        Assert.Contains("FAH: online | FOLDING: false | Configured to fold: false", output.ToString());
        Assert.Null(host.Service.GetStatus());
    }
}