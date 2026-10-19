// Copyright (c) 2026 smallketchup82 <ketchup@smkt.ca>. Licensed under the MIT License.
// See the LICENSE file in the repository root for full license text.

using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TempFold.Integrations;

// Owns WebSocket framing and timeouts. The service owns all state and control decisions.
internal sealed class FahConnection : IDisposable
{
    private readonly ClientWebSocket socket = new();
    private readonly byte[] buffer = new byte[8192];

    public async Task<JsonNode?> ConnectAsync(Uri endpoint, CancellationToken token)
    {
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
        socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(10);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        await socket.ConnectAsync(endpoint, timeout.Token);

        return await ReceiveAsync(timeout.Token);
    }

    public async Task SetFoldingAsync(bool folding, CancellationToken token)
    {
        ReadOnlyMemory<byte> command = folding
            ? "{\"cmd\":\"state\",\"state\":\"fold\"}"u8.ToArray()
            : "{\"cmd\":\"state\",\"state\":\"pause\"}"u8.ToArray();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        await socket.SendAsync(
            command,
            WebSocketMessageType.Text,
            true,
            timeout.Token);
    }

    public async Task<JsonNode?> ReceiveAsync(CancellationToken token)
    {
        using var message = new MemoryStream();
        ValueWebSocketReceiveResult result;

        do
        {
            result = await socket.ReceiveAsync(buffer.AsMemory(), token);

            if (result.MessageType == WebSocketMessageType.Close)
                throw new IOException("Client closed the WebSocket.");

            if (result.MessageType != WebSocketMessageType.Text)
                throw new JsonException("Expected a text WebSocket message.");

            message.Write(buffer, 0, result.Count);

            if (message.Length > 8 * 1024 * 1024)
                throw new JsonException("Client message exceeds 8 MiB.");
        } while (!result.EndOfMessage);

        return JsonNode.Parse(message.GetBuffer().AsSpan(0, (int)message.Length));
    }

    public async Task CloseAsync(CancellationToken token)
    {
        // Direct CLI commands close gracefully so disposal cannot discard the
        // command bytes before the client receives them. Cleanup stays bounded.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(1));
        try
        {
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", timeout.Token);
        }
        catch (Exception exception) when (exception is WebSocketException or IOException
            or OperationCanceledException or InvalidOperationException)
        {
            // A command was already sent (or status was read). A missing close
            // acknowledgement does not turn that completed action into a failure.
        }
    }

    public void Dispose()
    {
        socket.Abort();
        socket.Dispose();
    }
}
