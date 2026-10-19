// Copyright (c) 2026 smallketchup82 <ketchup@smkt.ca>. Licensed under the MIT License.
// See the LICENSE file in the repository root for full license text.

using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using TempFold.Api;
using TempFold.Control;
using TempFold.Models;
using TempFold.Sensors;
using TempFold.Tests.Support;

namespace TempFold.Tests;

[Trait("Category", "Integration")]
public sealed class ControlRecoveryProbeTests
{
    [Theory]
    [InlineData(true, 18)]
    [InlineData(true, 25)]
    [InlineData(false, 18)]
    [InlineData(false, 25)]
    public async Task PauseInhibitsAndResumeSynchronizesWithoutWaitingForAnotherReading(bool manual, double temperature)
    {
        await using var probe = await Probe.CreateAsync(20, 1);

        await probe.PostAsync(18);

        await probe.ExpectAsync("fold");

        probe.Pause(manual, true);

        await probe.ExpectAsync("pause");

        await probe.PostAsync(temperature);

        await probe.Client.AssertNoCommandAsync();

        // Also check external starts while inhibited, including hot-room resume.
        await probe.Client.ConfirmFoldingAndStartWorkAsync();
        await probe.Host.WaitForStatusAsync(new(true, true));
        await probe.PostAsync(temperature);

        await probe.Client.AssertNoCommandAsync();

        probe.Pause(manual, false);
        if (temperature >= 21)
            await probe.ExpectAsync("pause");
        else
            await probe.Client.AssertNoCommandAsync(); // Already folding.
        await probe.PostAsync(temperature);

        await probe.Client.AssertNoCommandAsync();
        Assert.False(probe.Controller.GetSnapshot().Paused);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CoolResumeRestartsPausedClientImmediately(bool manual)
    {
        await using var probe = await Probe.CreateAsync(20, 1);

        await probe.PostAsync(18);

        await probe.ExpectAsync("fold");

        probe.Pause(manual, true);

        await probe.ExpectAsync("pause");

        await probe.PostAsync(18);

        await probe.Client.AssertNoCommandAsync();

        probe.Pause(manual, false);

        await probe.ExpectAsync("fold");

        await probe.PostAsync(18);

        await probe.Client.AssertNoCommandAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TargetChangeThroughInteractiveInputStopsImmediatelyOrOnResume(bool paused)
    {
        await using var probe = await Probe.CreateAsync(28, 1);

        await probe.PostAsync(25);

        await probe.ExpectAsync("fold");

        if (paused)
        {
            probe.Pause(true, true);

            await probe.ExpectAsync("pause");

            await probe.Client.ConfirmFoldingAndStartWorkAsync();
            await probe.Host.WaitForStatusAsync(new(true, true));
        }

        probe.Edit(ConsoleKey.T, "23");

        Assert.Equal(23, probe.Controller.GetSnapshot().Settings.Target);

        if (paused)
        {
            await probe.Client.AssertNoCommandAsync();

            probe.Pause(true, false);
        }

        await probe.ExpectAsync("pause"); // No new sensor reading since the edit.

        probe.Edit(ConsoleKey.T, "28");

        await probe.ExpectAsync("fold");
    }

    [Fact]
    public async Task RangeChangesReevaluateImmediatelyAndMiddleBandPreservesIntent()
    {
        await using var probe = await Probe.CreateAsync(28, 1);

        await probe.PostAsync(25);

        await probe.ExpectAsync("fold");

        probe.Edit(ConsoleKey.R, "4"); // 25 is now inside 24..32: retain folding.

        await probe.Client.AssertNoCommandAsync();

        probe.Edit(ConsoleKey.T, "23"); // Inside 19..27: still retain folding.

        await probe.Client.AssertNoCommandAsync();

        probe.Edit(ConsoleKey.R, "1"); // Stop boundary becomes 24.

        await probe.ExpectAsync("pause");

        probe.Edit(ConsoleKey.T, "28");

        await probe.ExpectAsync("fold");
    }

    [Fact]
    public async Task EditingBeforeFirstReadingWaitsForTemperatureThenUsesNewSettings()
    {
        await using var probe = await Probe.CreateAsync(28, 1);

        probe.Edit(ConsoleKey.T, "23");
        probe.Edit(ConsoleKey.R, "0.5");

        await probe.Client.AssertNoCommandAsync();

        await probe.PostAsync(25);

        await probe.Client.AssertNoCommandAsync(); // Already paused and too hot.

        await probe.PostAsync(22);

        await probe.ExpectAsync("fold");
    }

    private sealed class Probe : IAsyncDisposable
    {
        public required FahServiceTestHost Host;
        public required FahTestSession Client;
        public required TemperatureController Controller;
        public required WebApplication App;
        public required HttpClient Http;
        private bool gameMode;

        public static async Task<Probe> CreateAsync(double target, double range)
        {
            var host = await FahServiceTestHost.CreateAsync(o => o with
            {
                Target = target,
                Range = range,
                AllowedTransmissionSources = ["api"],
                PauseFahOnExit = false
            });
            var client = await host.StartServiceAsync();
            Probe? probe = null;
            var controller = new TemperatureController(
                host.Options,
                () => new GameModeStatus(probe?.gameMode ?? false, null),
                host.Service.SetFolding,
                host.Service.GetStatus,
                TimeProvider.System,
                host.Service.SetPaused,
                host.Service.SetFoldingManually);
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            builder.Services.ConfigureHttpJsonOptions(o =>
                o.SerializerOptions.TypeInfoResolverChain.Insert(0, SensorJsonContext.Default));
            builder.Services.AddSingleton(host.Options);
            builder.Services.AddSingleton(controller);
            builder.Services.AddSingleton<SensorInputService>();
            builder.Services.AddHostedService(p => p.GetRequiredService<SensorInputService>());
            var app = builder.Build();
            app.MapSensorEndpoints();
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses
                .Single();
            probe = new Probe
            {
                Host = host,
                Client = client,
                Controller = controller,
                App = app,
                Http = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(5) }
            };
            return probe;
        }

        public async Task PostAsync(double temperature)
        {
            int before = Controller.GetSnapshot().History.Count;
            using var body = new StringContent(
                $"{{\"temperature\":{temperature.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}",
                Encoding.UTF8,
                "application/json");
            using var response = await Http.PostAsync("/sensor", body);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (Controller.GetSnapshot().History.Count <= before)
                await Task.Delay(10, timeout.Token);
        }

        public void Pause(bool manual, bool value)
        {
            if (manual)
                Controller.Pause(value, manual: true);
            else
            {
                gameMode = value;
                Controller.RefreshSchedule();
            }
        }

        public void Edit(ConsoleKey key, string value)
        {
            var input = new InteractiveTemperatureInput(Controller);
            input.HandleKey(
                new ConsoleKeyInfo(
                    key == ConsoleKey.T ? 't' : 'r',
                    key,
                    false,
                    false,
                    false));
            foreach (char character in value)
                input.HandleKey(
                    new ConsoleKeyInfo(
                        character,
                        ConsoleKey.NoName,
                        false,
                        false,
                        false));
            input.HandleKey(
                new ConsoleKeyInfo(
                    '\r',
                    ConsoleKey.Enter,
                    false,
                    false,
                    false));
            Assert.Null(input.Prompt);
        }

        public async Task ExpectAsync(string command)
        {
            Assert.Equal(command, await Client.ReadCommandAsync());
            if (command == "fold")
                await Client.ConfirmFoldingAndStartWorkAsync();
            else
                await Client.ConfirmPauseAsync();
            await Host.WaitForStatusAsync(command == "fold" ? new(true, true) : new(false, false));
        }

        public async ValueTask DisposeAsync()
        {
            Http.Dispose();
            await App.StopAsync();
            await App.DisposeAsync();
            await Host.DisposeAsync();
        }
    }
}