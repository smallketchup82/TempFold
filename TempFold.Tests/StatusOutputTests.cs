// Copyright (c) 2026 smallketchup82 <ketchup@smkt.ca>. Licensed under the MIT License.
// See the LICENSE file in the repository root for full license text.

using System.Text.Json;
using TempFold.Control;
using TempFold.Integrations;
using TempFold.Logging;

namespace TempFold.Tests;

public sealed class StatusOutputTests
{
    [Fact]
    public void NonInteractivePrintsFullLinesWithoutEscapesOrEffects()
    {
        using var output = new StringWriter();
        using var console = new StatusConsole(
            new StatusOutputOptions(Interactive: false, Colors: false, Effects: true),
            output);
        var line = new StatusLine(new StatusSegment(new string('x', 300), StatusColor.GreenColor));

        console.Update(line, sensorUpdate: true);
        console.Update(line);
        console.Clear();

        Assert.Equal(
            new string('x', 300) + Environment.NewLine + new string('x', 300) + Environment.NewLine,
            output.ToString());
        Assert.False(console.EffectsEnabled);
    }

    [Fact]
    public async Task SensorIngestDoesNotPrintExtraJsonLinesBetweenScheduledTicks()
    {
        using var output = new StringWriter();
        var options = new ControllerOptions(
            20,
            1,
            "unused",
            115200,
            new Uri("ws://localhost/api/websocket"))
        {
            Status = new StatusOutputOptions(
                Interactive: false,
                Json: true,
                Effects: true,
                IntervalSeconds: 5)
        };
        using var console = new StatusConsole(options.Status, output);
        using var folding = new FahService(
            options,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<FahService>.Instance);
        var controller = new TemperatureController(
            options,
            () => new Models.GameModeStatus(false, null),
            _ =>
            {
            },
            () => null,
            TimeProvider.System);

        controller.ProcessReading(20);
        controller.Pause(true, manual: true);
        using var service = new ControllerStatusService(
            controller,
            console,
            folding,
            options);

        await service.StartAsync(CancellationToken.None);
        try
        {
            // Allow the independent display loop to publish its initial snapshot.
            for (int attempt = 0; attempt < 100 && output.GetStringBuilder().Length == 0; attempt++)
                await Task.Delay(10);
            controller.ProcessReading(21);
            await Task.Delay(50);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        string[] lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        string json = Assert.Single(lines);
        using JsonDocument parsed = JsonDocument.Parse(json);

        Assert.Equal(20, parsed.RootElement.GetProperty("temperature").GetDouble());
        Assert.Equal(JsonValueKind.Null, parsed.RootElement.GetProperty("trend5Minutes").ValueKind);
        Assert.True(parsed.RootElement.GetProperty("paused").GetBoolean());
        Assert.Equal("manual", parsed.RootElement.GetProperty("pauseReason").GetString());
        Assert.Equal(JsonValueKind.Null, parsed.RootElement.GetProperty("foldingActive").ValueKind);
        Assert.DoesNotContain('\u001b', json);
    }
}