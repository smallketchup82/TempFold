// Copyright (c) 2026 smallketchup82 <ketchup@smkt.ca>. Licensed under the MIT License.
// See the LICENSE file in the repository root for full license text.

using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace TempFold.Tests.Support;

// Commands belong to a specific connection, so a reconnect cannot hide duplicate sends.
internal sealed class FahTestSession(WebSocket socket) : IDisposable
{
    private static readonly TimeSpan operationTimeout = TimeSpan.FromSeconds(8);
    private readonly object lifetimeLock = new();
    private readonly Channel<string> commands = Channel.CreateUnbounded<string>();
    private readonly TaskCompletionSource disconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsDisconnected => disconnected.Task.IsCompleted;
    public bool HasUnreadCommands => commands.Reader.TryPeek(out _);

    public async Task<string> ReadCommandAsync()
    {
        using var cancellation = new CancellationTokenSource(operationTimeout);
        return await commands.Reader.ReadAsync(cancellation.Token);
    }

    public async Task AssertNoCommandAsync()
    {
        using var observation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));

        try
        {
            string command = await commands.Reader.ReadAsync(observation.Token);
            Assert.Fail($"Unexpected FAH command: {command}.");
        }
        catch (OperationCanceledException) when (observation.IsCancellationRequested)
        {
            // No command arrived during the observation window.
        }
    }

    public async Task SetGroupPausedAsync(string group, bool paused)
    {
        var update = new JsonArray(
            "groups",
            group,
            "config",
            "paused",
            paused);
        await SendTextAsync(update.ToJsonString());
    }

    public async Task ConfirmFoldingAndStartWorkAsync()
    {
        await SetGroupPausedAsync("", paused: false);
        await SetGroupPausedAsync("gpu", paused: false);
        await SendTextAsync(
            """
            ["units", -1, {"group": "gpu", "state": "RUN", "paused": false}]
            """);
        // Observing this unit working proves both preceding configuration updates were processed.
    }

    public async Task ConfirmPauseAsync()
    {
        await SetGroupPausedAsync("", paused: true);
        await SetGroupPausedAsync("gpu", paused: true);
    }

    public async Task SendTextAsync(string text)
    {
        using var cancellation = new CancellationTokenSource(operationTimeout);
        await socket.SendAsync(
            Encoding.UTF8.GetBytes(text).AsMemory(),
            WebSocketMessageType.Text,
            true,
            cancellation.Token);
    }

    public async Task SendInitialSnapshotAsync(CancellationToken cancellationToken)
    {
        const string snapshot = """
                                {
                                  "groups": {
                                    "": {"config": {"paused": true, "finish": false}},
                                    "gpu": {"config": {"paused": true, "finish": false}}
                                  },
                                  "units": []
                                }
                                """;
        byte[] bytes = Encoding.UTF8.GetBytes(snapshot);
        int midpoint = bytes.Length / 2;

        await socket.SendAsync(
            bytes.AsMemory(0, midpoint),
            WebSocketMessageType.Text,
            false,
            cancellationToken);
        await socket.SendAsync(
            bytes.AsMemory(midpoint),
            WebSocketMessageType.Text,
            true,
            cancellationToken);
    }

    public async Task ReceiveCommandsAsync(CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[4096];

        while (true)
        {
            using var message = new MemoryStream();
            ValueWebSocketReceiveResult result;

            do
            {
                result = await socket.ReceiveAsync(buffer.AsMemory(), cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }

                Assert.Equal(WebSocketMessageType.Text, result.MessageType);
                message.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            JsonNode command = JsonNode.Parse(message.GetBuffer().AsSpan(0, (int)message.Length))!;
            Assert.Equal("state", command["cmd"]!.GetValue<string>());
            await commands.Writer.WriteAsync(command["state"]!.GetValue<string>(), cancellationToken);
        }
    }

    public void Disconnect()
    {
        lock (lifetimeLock)
        {
            if (!IsDisconnected)
            {
                socket.Abort();
            }
        }
    }

    public Task WaitForDisconnectAsync() => disconnected.Task.WaitAsync(operationTimeout);

    public void Dispose()
    {
        lock (lifetimeLock)
        {
            if (IsDisconnected)
            {
                return;
            }

            socket.Dispose();
            commands.Writer.TryComplete();
            disconnected.TrySetResult();
        }
    }
}