// Copyright (c) 2026 smallketchup82 <ketchup@smkt.ca>. Licensed under the MIT License.
// See the LICENSE file in the repository root for full license text.

using System.Net.WebSockets;
using TempFold.CommandLine;
using TempFold.Configuration;

namespace TempFold.Tests;

public sealed class FahCommandTests : IDisposable
{
    private readonly string temporary = Path.Combine(
        Path.GetTempPath(),
        "tempfold-fah-command-" + Guid.NewGuid().ToString("N"));

    private string ConfigPath => Path.Combine(temporary, "config.json");

    public FahCommandTests() => Directory.CreateDirectory(temporary);

    [Theory]
    [InlineData("start")]
    [InlineData("stop")]
    [InlineData("status")]
    public async Task ActionsUseDefaultEndpointWithoutTargetOrCreatingConfiguration(string action)
    {
        var calls = new List<(string, Uri)>();

        Assert.Equal(
            0,
            await FahCommand.InvokeAsync(
                [action],
                ConfigPath,
                (command, endpoint, _) =>
                {
                    calls.Add((command, endpoint));
                    return Task.CompletedTask;
                }));
        Assert.Equal((action, new Uri(AppConfig.DefaultFahEndpoint)), Assert.Single(calls));
        Assert.False(File.Exists(ConfigPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfigEndpointCanBeOverriddenBeforeOrAfterAction(bool before)
    {
        await File.WriteAllTextAsync(ConfigPath, """{"fahEndpoint":"ws://configured:7396/api/websocket"}""");
        Uri? selected = null;

        Task Run(string _, Uri endpoint, CancellationToken __)
        {
            selected = endpoint;
            return Task.CompletedTask;
        }

        Assert.Equal(0, await FahCommand.InvokeAsync(["status"], ConfigPath, Run));
        Assert.Equal(new Uri("ws://configured:7396/api/websocket"), selected);

        string[] options = ["--config", ConfigPath, "--fah-endpoint", "wss://override/socket"];

        Assert.Equal(
            0,
            await FahCommand.InvokeAsync(
                before ? [.. options, "start"] : ["start", .. options],
                Path.Combine(temporary, "missing.json"),
                Run));
        Assert.Equal(new Uri("wss://override/socket"), selected);
    }

    [Theory]
    [InlineData("http://localhost/socket")]
    [InlineData("ws://user:password@localhost/socket")]
    public async Task InvalidEndpointDoesNotConnect(string endpoint)
    {
        var error = new StringWriter();

        Assert.Equal(
            1,
            await FahCommand.InvokeAsync(
                ["start", "--fah-endpoint", endpoint],
                ConfigPath,
                (_, _, _) => throw new Exception("Must not connect."),
                error: error));
        Assert.Contains("ws:// or wss://", error.ToString());
    }

    [Fact]
    public async Task ExplicitMissingConfigDoesNotConnect()
    {
        var error = new StringWriter();

        Assert.Equal(
            1,
            await FahCommand.InvokeAsync(
                ["stop", "--config", ConfigPath],
                run:
                (_, _, _) => throw new Exception("Must not connect."),
                error: error));
        Assert.Contains("FAH command failed", error.ToString());
    }

    [Fact]
    public async Task ConnectionFailureReturnsAnErrorWithoutStartingARetryLoop()
    {
        var error = new StringWriter();
        int calls = 0;

        Assert.Equal(
            1,
            await FahCommand.InvokeAsync(
                ["start"],
                ConfigPath,
                (_, _, _) =>
                {
                    calls++;
                    throw new WebSocketException("offline");
                },
                error: error));
        Assert.Equal(1, calls);
        Assert.Contains("offline", error.ToString());
    }

    public void Dispose() => Directory.Delete(temporary, recursive: true);
}