// Copyright (c) 2026 smallketchup82 <ketchup@smkt.ca>. Licensed under the MIT License.
// See the LICENSE file in the repository root for full license text.

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using TempFold.Control;
using TempFold.Integrations;
using TempFold.Models;
using TempFold.Sensors;
using TempFold.Tests.Support;

namespace TempFold.Tests;

public sealed class RuntimeLifetimeTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TimeoutIncludesStartupWithoutAnyDataOrConnection(bool sensor)
    {
        var clock = new Clock();
        var lifetime = new Lifetime();
        var options = Options() with
        {
            SensorTimeoutSeconds = sensor ? 10 : 0, FahConnectionTimeoutSeconds = sensor ? 0 : 10
        };
        using var folding = new FahService(options, NullLogger<FahService>.Instance);
        var controller = Controller(options);
        using var monitor = Monitor(
            options,
            controller,
            folding,
            lifetime,
            clock);

        await monitor.StartAsync(CancellationToken.None);
        clock.Advance(10);
        await lifetime.WaitForStopAsync();
        await monitor.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ValidSensorReadingsResetDeadline()
    {
        var clock = new Clock();
        var lifetime = new Lifetime();
        var options = Options() with { SensorTimeoutSeconds = 10, FahConnectionTimeoutSeconds = 0 };
        using var folding = new FahService(options, NullLogger<FahService>.Instance);
        var controller = Controller(options);
        using var input = new SensorInputService(
            options with { AllowedTransmissionSources = ["api"] },
            controller,
            NullLogger<SensorInputService>.Instance);
        using var monitor = Monitor(
            options,
            controller,
            folding,
            lifetime,
            clock,
            input);

        await monitor.StartAsync(CancellationToken.None);
        await input.StartAsync(CancellationToken.None);
        clock.Advance(9);

        Assert.True(input.TryEnqueue(22));

        clock.Advance(9);
        await AssertStillRunningAsync(lifetime);
        clock.Advance(1);
        await lifetime.WaitForStopAsync();
        await input.StopAsync(CancellationToken.None);
        await monitor.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ZeroTimeoutsAllowIndefiniteOperation()
    {
        var clock = new Clock();
        var lifetime = new Lifetime();
        var options = Options() with { SensorTimeoutSeconds = 0, FahConnectionTimeoutSeconds = 0 };
        using var folding = new FahService(options, NullLogger<FahService>.Instance);
        using var monitor = Monitor(
            options,
            Controller(options),
            folding,
            lifetime,
            clock);

        await monitor.StartAsync(CancellationToken.None);
        clock.Advance(86400);
        await AssertStillRunningAsync(lifetime);
        await monitor.StopAsync(CancellationToken.None);
    }

    [Theory]
    [Trait("Category", "Integration")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DisconnectEitherQuitsImmediatelyOrStartsANewTimeout(bool quit)
    {
        await using var host = await FahServiceTestHost.CreateAsync(o => o with { PauseFahOnExit = false });
        var clock = new Clock();
        var lifetime = new Lifetime();
        var options = host.Options with { QuitOnFahDisconnect = quit, FahConnectionTimeoutSeconds = 10 };
        using var monitor = Monitor(
            options,
            Controller(options),
            host.Service,
            lifetime,
            clock);

        await monitor.StartAsync(CancellationToken.None);
        FahTestSession client = await host.StartServiceAsync();

        clock.Advance(100);
        await AssertStillRunningAsync(lifetime); // A connected session never consumes the offline timeout.
        client.Disconnect();
        await host.WaitForStatusAsync(null);
        if (!quit)
        {
            clock.Advance(9);
            await AssertStillRunningAsync(lifetime);
            clock.Advance(1);
        }

        await lifetime.WaitForStopAsync();
        await monitor.StopAsync(CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ReconnectionClearsTheOfflineDeadline()
    {
        await using var host = await FahServiceTestHost.CreateAsync(o => o with { PauseFahOnExit = false });
        var clock = new Clock();
        var lifetime = new Lifetime();
        var options = host.Options with { FahConnectionTimeoutSeconds = 10 };
        using var monitor = Monitor(
            options,
            Controller(options),
            host.Service,
            lifetime,
            clock);

        await monitor.StartAsync(CancellationToken.None);
        FahTestSession client = await host.StartServiceAsync();

        client.Disconnect();
        await host.WaitForStatusAsync(null);
        clock.Advance(9);
        await host.WaitForConnectionAsync();
        await host.WaitForStatusAsync(new FoldingStatus(false, false));
        clock.Advance(100);
        await AssertStillRunningAsync(lifetime);
        await monitor.StopAsync(CancellationToken.None);
    }

    private static ControllerOptions Options() =>
        new(
            22,
            3,
            "unused",
            115200,
            new Uri("ws://localhost/api/websocket"));

    private static TemperatureController Controller(ControllerOptions options) =>
        new(
            options,
            () => new GameModeStatus(false, null),
            _ =>
            {
            },
            () => null,
            TimeProvider.System);

    private static RuntimeLifetimeService Monitor(
        ControllerOptions options,
        TemperatureController controller,
        FahService folding,
        Lifetime lifetime,
        Clock clock,
        SensorInputService? input = null) =>
        new(
            options,
            input ?? new SensorInputService(options, controller, NullLogger<SensorInputService>.Instance),
            folding,
            lifetime,
            clock,
            NullLogger<RuntimeLifetimeService>.Instance);

    private static async Task AssertStillRunningAsync(Lifetime lifetime)
    {
        await Task.Delay(250);
        Assert.False(lifetime.ApplicationStopping.IsCancellationRequested);
    }

    private sealed class Clock : TimeProvider
    {
        private long timestamp;
        public override long TimestampFrequency => 1000;

        public override long GetTimestamp() => Interlocked.Read(ref timestamp);

        public void Advance(long seconds) => Interlocked.Add(ref timestamp, seconds * 1000);
    }

    private sealed class Lifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource stopping = new();
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication() => stopping.Cancel();

        public async Task WaitForStopAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while (!stopping.IsCancellationRequested)
                await Task.Delay(10, timeout.Token);
        }
    }
}