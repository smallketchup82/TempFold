// Copyright (c) 2026 smallketchup82 <ketchup@smkt.ca>. Licensed under the MIT License.
// See the LICENSE file in the repository root for full license text.

using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TempFold.Control;
using TempFold.Integrations;
using TempFold.Models;

namespace TempFold.Tests.Support;

// Each test owns its server, service, and connections. No real FAH client is involved.
internal sealed class FahServiceTestHost : IAsyncDisposable
{
    private static readonly TimeSpan operationTimeout = TimeSpan.FromSeconds(8);
    private readonly WebApplication server;
    private readonly Channel<FahTestSession> connections = Channel.CreateUnbounded<FahTestSession>();
    private readonly ConcurrentBag<FahTestSession> sessions = [];

    public FahService Service { get; private set; } = null!;
    public ControllerOptions Options { get; private set; } = null!;

    private FahServiceTestHost()
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        server = builder.Build();
        server.UseWebSockets();
        server.Map("/api/websocket", AcceptClientAsync);
    }

    public static async Task<FahServiceTestHost> CreateAsync(
        Func<ControllerOptions, ControllerOptions>? configure = null)
    {
        var host = new FahServiceTestHost();

        try
        {
            await host.server.StartAsync();

            string address = host.server.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            var endpoint = new UriBuilder(address) { Scheme = "ws", Path = "/api/websocket" };
            var options = new ControllerOptions(
                22,
                3,
                "/unused",
                115200,
                endpoint.Uri);
            if (configure is not null)
                options = configure(options);
            host.Options = options;
            host.Service = new FahService(options, NullLogger<FahService>.Instance);

            return host;
        }
        catch
        {
            await host.server.DisposeAsync();
            throw;
        }
    }

    public async Task<FahTestSession> StartServiceAsync()
    {
        await Service.StartAsync(CancellationToken.None);
        FahTestSession client = await WaitForConnectionAsync();
        await WaitForStatusAsync(new FoldingStatus(false, false));
        return client;
    }

    public async Task<FahTestSession> WaitForConnectionAsync(TimeSpan? timeout = null)
    {
        using var cancellation = new CancellationTokenSource(timeout ?? operationTimeout);
        return await connections.Reader.ReadAsync(cancellation.Token);
    }

    public async Task WaitForStatusAsync(FoldingStatus? expected)
    {
        using var cancellation = new CancellationTokenSource(operationTimeout);

        try
        {
            while (Service.GetStatus() != expected)
            {
                await Task.Delay(20, cancellation.Token);
            }
        }
        catch (OperationCanceledException)
        {
            string expectedStatus = expected?.ToString() ?? "offline";
            string actualStatus = Service.GetStatus()?.ToString() ?? "offline";
            Assert.Fail($"Expected FAH status {expectedStatus}; actual status was {actualStatus}.");
        }
    }

    public async Task StopServiceAsync()
    {
        using var cancellation = new CancellationTokenSource(operationTimeout);
        await Service.StopAsync(cancellation.Token);
    }

    private async Task AcceptClientAsync(HttpContext context)
    {
        using var client = new FahTestSession(await context.WebSockets.AcceptWebSocketAsync());
        sessions.Add(client);

        try
        {
            await client.SendInitialSnapshotAsync(context.RequestAborted);
            await connections.Writer.WriteAsync(client, context.RequestAborted);
            await client.ReceiveCommandsAsync(context.RequestAborted);
        }
        catch (Exception exception) when (
            exception is System.Net.WebSockets.WebSocketException or OperationCanceledException)
        {
            // Disconnects are expected in the recovery tests and during fixture cleanup.
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopServiceAsync();
        }
        finally
        {
            Service.Dispose();

            foreach (FahTestSession client in sessions)
            {
                client.Disconnect();
            }

            try
            {
                using var cancellation = new CancellationTokenSource(operationTimeout);
                await server.StopAsync(cancellation.Token);
            }
            finally
            {
                await server.DisposeAsync();
            }
        }
    }
}