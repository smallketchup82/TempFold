// Copyright (c) 2026 smallketchup82 <ketchup@smkt.ca>. Licensed under the MIT License.
// See the LICENSE file in the repository root for full license text.

using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Logging;
using TempFold.CommandLine;
using TempFold.Control;
using TempFold.Logging;
using TempFold.Models;
using System.Text.Json;

namespace TempFold.Tests;

public sealed class DiagnosticLoggingTests
{
    [Fact]
    public void SensorPauseAndEditsProduceStructuredDiagnosticEvents()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".jsonl");
        try
        {
            var configuration = new LoggerConfiguration().MinimumLevel.Verbose()
                .Enrich.WithProperty("sessionId", "test-session");

            FileLogging.Configure(configuration, path);
            using (var logger = configuration.CreateLogger())
            using (var factory = new SerilogLoggerFactory(logger, dispose: false))
            {
                var controller = new TemperatureController(
                    new(
                        20,
                        1,
                        "unused",
                        115200,
                        new Uri("ws://localhost")),
                    () => new(false, null),
                    _ =>
                    {
                    },
                    () => new FoldingStatus(false, false),
                    TimeProvider.System,
                    logger: factory.CreateLogger<TemperatureController>());

                controller.Pause(true, manual: true);
                controller.ProcessReading(18.524, "api");
                controller.SetTargetOverride(23);
                controller.SetRangeOverride(2);
                controller.Pause(false, manual: true);
                controller.ProcessReading(18.5, "serial");
            }

            var events = File.ReadAllLines(path).Select(line => JsonDocument.Parse(line)).ToArray();
            try
            {
                var readings = events.Where(x => x.RootElement.GetProperty("logType").GetString() == "sensorUpdate")
                    .ToArray();

                Assert.Equal(2, readings.Length);

                var inhibited = readings[0].RootElement;

                Assert.Equal("inhibited", inhibited.GetProperty("decision").GetString());
                Assert.Equal("manual", inhibited.GetProperty("pauseReason").GetString());
                Assert.Equal(18.5, inhibited.GetProperty("temperature").GetDouble());
                Assert.Equal(19, inhibited.GetProperty("startThreshold").GetDouble());
                Assert.Equal(21, inhibited.GetProperty("stopThreshold").GetDouble());
                Assert.False(inhibited.GetProperty("folding").GetBoolean());
                Assert.True(inhibited.GetProperty("desiredFolding").GetBoolean());
                Assert.Equal("requestStart", readings[1].RootElement.GetProperty("decision").GetString());
                Assert.Equal("serial", readings[1].RootElement.GetProperty("source").GetString());
                Assert.All(
                    events,
                    x => Assert.Equal("test-session", x.RootElement.GetProperty("sessionId").GetString()));
                Assert.Contains(
                    events,
                    x => x.RootElement.GetProperty("logType").GetString() == "userInput"
                         && x.RootElement.GetProperty("action").GetString() == "setRange");
            }
            finally
            {
                foreach (var entry in events)
                    entry.Dispose();
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ParsesLoggingOptionsAndDefaults()
    {
        ControllerOptions? options = null;

        Assert.Equal(
            0,
            await ControllerCommand.InvokeAsync(
                ["--target", "20", "--log-directory", "default"],
                (o, _) =>
                {
                    options = o;
                    return Task.CompletedTask;
                }));
        Assert.Equal("verbose", options!.LogLevel);
        Assert.Equal("information", options.Verbosity);
        Assert.Equal("default", options.LogDirectory);
        Assert.Equal(31, options.LogRetainedFiles);
        Assert.Equal(20971520, options.LogMaxSizeBytes);
        Assert.Equal(
            0,
            await ControllerCommand.InvokeAsync(
                [
                    "--target",
                    "20",
                    "--verbosity",
                    "debug",
                    "--log-level",
                    "warning",
                    "--log-max-size",
                    "1024",
                    "--log-retained-files",
                    "2"
                ],
                (o, _) =>
                {
                    options = o;
                    return Task.CompletedTask;
                }));
        Assert.Equal("debug", options!.Verbosity);
        Assert.Equal("warning", options.LogLevel);
        Assert.Equal(1024, options.LogMaxSizeBytes);
        Assert.Equal(2, options.LogRetainedFiles);
    }

    [Fact]
    public async Task ExplicitCliLoggingSettingsOverrideConfig()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            await File.WriteAllTextAsync(
                path,
                """
                {"logDirectory":"default","verbosity":"warning","logLevel":"debug","logMaxSizeBytes":1024,"logRetainedFiles":2}
                """);
            ControllerOptions? options = null;

            Assert.Equal(
                0,
                await ControllerCommand.InvokeAsync(
                    [
                        "--target",
                        "20",
                        "--config",
                        path,
                        "--verbosity",
                        "information",
                        "--log-level",
                        "verbose",
                        "--log-max-size",
                        "2048",
                        "--log-retained-files",
                        "4"
                    ],
                    (o, _) =>
                    {
                        options = o;
                        return Task.CompletedTask;
                    }));
            Assert.Equal("default", options!.LogDirectory);
            Assert.Equal("information", options.Verbosity);
            Assert.Equal("verbose", options.LogLevel);
            Assert.Equal(2048, options.LogMaxSizeBytes);
            Assert.Equal(4, options.LogRetainedFiles);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("--log-file a --log-directory b")]
    [InlineData("--log-level nonsense")]
    [InlineData("--verbosity 42")]
    [InlineData("--log-max-size 0")]
    [InlineData("--log-retained-files -1")]
    public async Task RejectsInvalidLoggingOptions(string arguments)
    {
        Assert.Equal(
            1,
            await ControllerCommand.InvokeAsync(
                ["--target", "20", .. arguments.Split(' ')],
                (_, _) => throw new InvalidOperationException("Invalid arguments must not start the controller.")));
    }
}