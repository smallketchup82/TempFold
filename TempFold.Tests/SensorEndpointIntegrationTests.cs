// Copyright (c) 2026 smallketchup82 <ketchup@smkt.ca>. Licensed under the MIT License.
// See the LICENSE file in the repository root for full license text.

using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TempFold.Api;
using TempFold.Control;
using TempFold.Models;
using TempFold.Sensors;

namespace TempFold.Tests;

public sealed class SensorEndpointIntegrationTests
{
    [Theory]
    [Trait("Category", "Integration")]
    [InlineData("{}", "application/json", HttpStatusCode.BadRequest)]
    [InlineData("{\"temperature\":null}", "application/json", HttpStatusCode.BadRequest)]
    [InlineData("{invalid", "application/json", HttpStatusCode.BadRequest)]
    [InlineData("{\"temperature\":\"hot\"}", "application/json", HttpStatusCode.BadRequest)]
    [InlineData("{\"temperature\":22}", "text/plain", HttpStatusCode.UnsupportedMediaType)]
    [InlineData("{\"temperature\":22}", "application/json", HttpStatusCode.ServiceUnavailable)]
    public async Task ValidatesRequestsAndRejectsReadingsWhenInputIsUnavailable(
        string json,
        string contentType,
        HttpStatusCode expected)
    {
        await using WebApplication app = createApp(out _);

        await app.StartAsync();
        using HttpClient client = createClient(app);
        using var content = new StringContent(json, Encoding.UTF8, contentType);
        using HttpResponseMessage response = await client.PostAsync("/sensor", content);

        Assert.Equal(expected, response.StatusCode);

        if (json is "{}" or "{\"temperature\":null}")
            Assert.Equal("""{"error":"Temperature is required."}""", await response.Content.ReadAsStringAsync());
        await app.StopAsync();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ApiFallbackQueuesReadingAndStopsAcceptingAfterShutdown()
    {
        await using WebApplication app = createApp(out TemperatureController controller);
        SensorInputService input = app.Services.GetRequiredService<SensorInputService>();

        Assert.False(input.TryEnqueue(22));

        await input.StartAsync(CancellationToken.None);
        try
        {
            await app.StartAsync();
            using HttpClient client = createClient(app);
            using var content = new StringContent("""{"temperature":22.5}""", Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await client.PostAsync("/sensor", content);

            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (controller.GetSnapshot().Temperature is null)
                await Task.Delay(10, timeout.Token);

            Assert.Equal(22.5, controller.GetSnapshot().Temperature);
        }
        finally
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            await input.StopAsync(timeout.Token);
            await app.StopAsync(timeout.Token);
        }

        Assert.False(input.TryEnqueue(22));
    }

    [Theory]
    [Trait("Category", "Integration")]
    [InlineData("api", true)]
    [InlineData("serial", false)]
    public async Task OnlyAllowedSourcesCanSubmitApiReadings(string source, bool accepted)
    {
        await using WebApplication app = createApp(out TemperatureController controller, [source]);
        var input = app.Services.GetRequiredService<SensorInputService>();

        await input.StartAsync(CancellationToken.None);
        try
        {
            Assert.Equal(accepted, input.TryEnqueue(22));

            if (accepted)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                while (controller.GetSnapshot().Temperature is null)
                    await Task.Delay(10, timeout.Token);

                Assert.Equal(22, controller.GetSnapshot().Temperature);
            }
            else
                Assert.Null(controller.GetSnapshot().Temperature);
        }
        finally
        {
            await input.StopAsync(CancellationToken.None);
        }
    }

    private static WebApplication createApp(out TemperatureController controller, string[]? sources = null)
    {
        var options = new ControllerOptions(
            22,
            3,
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing-port"),
            115200,
            new Uri("ws://localhost/api/websocket"));
        if (sources is not null)
            options = options with { AllowedTransmissionSources = sources };
        controller = new TemperatureController(
            options,
            () => new GameModeStatus(false, null),
            _ =>
            {
            },
            () => null,
            TimeProvider.System);
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.TypeInfoResolverChain.Insert(0, SensorJsonContext.Default));
        var input = new SensorInputService(options, controller, NullLogger<SensorInputService>.Instance);
        builder.Services.AddSingleton<SensorInputService>(_ => input);
        WebApplication app = builder.Build();
        app.MapSensorEndpoints();
        return app;
    }

    private static HttpClient createClient(WebApplication app) =>
        new()
        {
            BaseAddress =
                new Uri(
                    app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses
                        .Single()),
            Timeout = TimeSpan.FromSeconds(5)
        };
}