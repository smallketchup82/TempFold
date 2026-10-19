// Copyright (c) 2026 smallketchup82 <ketchup@smkt.ca>. Licensed under the MIT License.
// See the LICENSE file in the repository root for full license text.

using TempFold.CommandLine;
using TempFold.Control;

namespace TempFold.Tests;

public sealed class ControllerCommandTests
{
    [Fact]
    public async Task SuppliesDefaultsAndThresholds()
    {
        ControllerOptions? options = null;
        int result = await ControllerCommand.InvokeAsync(
            ["--target", "22"],
            (value, _) =>
            {
                options = value;
                return Task.CompletedTask;
            });

        Assert.Equal(0, result);
        Assert.NotNull(options);
        Assert.Equal(22, options.Target);
        Assert.Equal(3, options.Range);
        Assert.Equal(19, options.StartThreshold);
        Assert.Equal(25, options.StopThreshold);
        Assert.Equal("/dev/ttyACM0", options.PortName);
        Assert.Equal(115200, options.BaudRate);
        Assert.Equal(new Uri("ws://127.0.0.1:7396/api/websocket"), options.FahEndpoint);
        Assert.True(options.Status.Interactive);
        Assert.False(options.Status.Effects);
        Assert.True(options.PauseOnGameMode);
        Assert.False(options.QuitOnFahDisconnect);
        Assert.True(options.PauseFahOnExit);
        Assert.Equal(0, options.SensorTimeoutSeconds);
        Assert.Equal(900, options.FahConnectionTimeoutSeconds);
        Assert.Equal(new[] { "api", "serial" }, options.AllowedTransmissionSources);
        Assert.Null(options.LogFile);
        Assert.False(options.StartPaused);
    }

    [Theory]
    [InlineData("--paused", true)]
    [InlineData("--paused false", false)]
    public async Task ParsesStartupPause(string arguments, bool expected)
    {
        ControllerOptions? options = null;

        Assert.Equal(
            0,
            await ControllerCommand.InvokeAsync(
                ["--target", "22", .. arguments.Split(' ')],
                (value, _) =>
                {
                    options = value;
                    return Task.CompletedTask;
                }));
        Assert.Equal(expected, options!.StartPaused);
    }

    [Fact]
    public async Task FahHelpDoesNotStartTheThermostatOrRequireATarget()
    {
        Assert.Equal(
            0,
            await ControllerCommand.InvokeAsync(
                ["fah", "--help"],
                (_, _) => throw new InvalidOperationException("Must not start thermostat.")));
    }

    [Fact]
    public async Task PassesExplicitOptionsToRunner()
    {
        ControllerOptions? options = null;
        int result = await ControllerCommand.InvokeAsync(
            [
                "--target",
                "-2",
                "--range",
                "1",
                "--port",
                "COM3",
                "--baud",
                "9600",
                "--fah-endpoint",
                "wss://example.com/socket"
            ],
            (value, _) =>
            {
                options = value;
                return Task.CompletedTask;
            });

        Assert.Equal(0, result);
        Assert.Equal(
            new ControllerOptions(
                -2,
                1,
                "COM3",
                9600,
                new Uri("wss://example.com/socket")),
            options);
    }

    [Theory]
    [InlineData("--duration", "0")]
    [InlineData("--duration", "NaN")]
    [InlineData("--status-interval", "5")]
    [InlineData("--range", "0")]
    [InlineData("--range", "-1")]
    [InlineData("--port", "")]
    [InlineData("--port", " ")]
    [InlineData("--baud", "0")]
    [InlineData("--baud", "-1")]
    [InlineData("--target", "invalid")]
    [InlineData("--fah-endpoint", "/socket")]
    [InlineData("--fah-endpoint", "http://localhost/socket")]
    [InlineData("--fah-endpoint", "ws://user:pass@localhost/socket")]
    [InlineData("--fah-endpoint", "ws://localhost/socket#fragment")]
    [InlineData("--unknown", "1")]
    [InlineData("--sensor-timeout", "-1")]
    [InlineData("--sensor-timeout", "NaN")]
    [InlineData("--fah-connection-timeout", "Infinity")]
    [InlineData("--allowed-transmission-sources", "bluetooth")]
    [InlineData("--log-file", " ")]
    public async Task RejectsInvalidOptionsWithoutRunning(string option, string value)
    {
        bool called = false;
        string[] args = option == "--target" ? [option, value] : ["--target", "22", option, value];
        int result = await ControllerCommand.InvokeAsync(
            args,
            (_, _) =>
            {
                called = true;
                return Task.CompletedTask;
            });

        Assert.NotEqual(0, result);
        Assert.False(called);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingTargetOrHelpDoesNotRun(bool help)
    {
        bool called = false;
        int result = await ControllerCommand.InvokeAsync(
            help ? ["--help"] : [],
            (_, _) =>
            {
                called = true;
                return Task.CompletedTask;
            });

        Assert.Equal(help, result == 0);
        Assert.False(called);
    }

    [Fact]
    public async Task JsonDefaultsToFiveSecondNonInteractiveOutput()
    {
        ControllerOptions? options = null;

        Assert.Equal(
            0,
            await ControllerCommand.InvokeAsync(
                ["--target", "20", "--json"],
                (value, _) =>
                {
                    options = value;
                    return Task.CompletedTask;
                }));
        Assert.True(options!.Status.Json);
        Assert.False(options.Status.Interactive);
        Assert.Equal(5, options.Status.IntervalSeconds);
    }

    [Fact]
    public async Task AcceptsNonInteractiveIntervalAndColorFlag()
    {
        ControllerOptions? options = null;

        Assert.Equal(
            0,
            await ControllerCommand.InvokeAsync(
                [
                    "--target",
                    "20",
                    "--non-interactive",
                    "--no-color",
                    "--status-interval",
                    "3"
                ],
                (value, _) =>
                {
                    options = value;
                    return Task.CompletedTask;
                }));
        Assert.False(options!.Status.Interactive);
        Assert.False(options.Status.Colors);
        Assert.Equal(3, options.Status.IntervalSeconds);
    }

    [Fact]
    public async Task EffectsAreOptInAndDurationIsRemoved()
    {
        ControllerOptions? options = null;

        Assert.Equal(
            0,
            await ControllerCommand.InvokeAsync(
                ["--target", "20", "--effects"],
                (value, _) =>
                {
                    options = value;
                    return Task.CompletedTask;
                }));
        Assert.True(options!.Status.Effects);
        Assert.NotEqual(
            0,
            await ControllerCommand.InvokeAsync(
                ["--target", "20", "--duration", "0.75"],
                (_, _) => throw new InvalidOperationException("Removed option must not run.")));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public async Task RejectsInvalidNonInteractiveIntervals(string interval)
    {
        bool called = false;

        Assert.NotEqual(
            0,
            await ControllerCommand.InvokeAsync(
                [
                    "--target",
                    "20",
                    "--non-interactive",
                    "--status-interval",
                    interval
                ],
                (_, _) =>
                {
                    called = true;
                    return Task.CompletedTask;
                }));
        Assert.False(called);
    }
}