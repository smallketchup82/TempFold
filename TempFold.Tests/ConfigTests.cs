// Copyright (c) 2026 smallketchup82 <ketchup@smkt.ca>. Licensed under the MIT License.
// See the LICENSE file in the repository root for full license text.

using System.Text.Json;
using TempFold.CommandLine;
using TempFold.Configuration;
using TempFold.Control;

namespace TempFold.Tests;

public sealed class ConfigTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(),
        "tempfold-config-" + Guid.NewGuid().ToString("N"));

    private string ConfigPath => Path.Combine(directory, "config.json");

    public ConfigTests() => Directory.CreateDirectory(directory);

    [Fact]
    public async Task ScheduleAndTimezoneLoadFromConfigAndCanBeDisabled()
    {
        await WriteAsync(
            """
            {"timeZone":"America/Toronto","schedule":[{"name":"schedule 1",
             "days":["monday","thursday","friday"],
             "blocks":[{"from":"02:10","to":"4:00","enabled":true,"target":23,"range":2}]}]}
            """);
        ControllerOptions? options = null;

        Task Run(ControllerOptions value, CancellationToken _)
        {
            options = value;
            return Task.CompletedTask;
        }

        Assert.Equal(0, await ControllerCommand.InvokeAsync(["--target", "20"], Run, ConfigPath));
        Assert.Equal("America/Toronto", options!.TimeZone!.Id);
        Assert.False(options.NoSchedule);
        Assert.Equal(23, Assert.Single(Assert.Single(options.Schedule!).Blocks).Target);
        Assert.Equal(0, await ControllerCommand.InvokeAsync(["--target", "20", "--no-schedule"], Run, ConfigPath));
        Assert.True(options!.NoSchedule);

        // Schedules can also be disabled without a config file.
        Assert.Equal(
            0,
            await ControllerCommand.InvokeAsync(
                ["--target", "20", "--no-schedule"],
                Run,
                Path.Combine(directory, "missing.json")));
        Assert.True(options!.NoSchedule);
    }

    [Theory]
    [InlineData("{\"schedule\":[]}")]
    [InlineData("{\"timeZone\":\"America/Toronto\"}")]
    [InlineData("{\"timeZone\":\"UTC\"}")]
    [InlineData("""{"schedule":[{"name":"empty","days":["monday"],"blocks":[]}]}""")]
    [InlineData(
        """{"schedule":[{"name":"night","days":["Sunday"],"blocks":[{"from":"22:00","to":"2:00","target":23,"range":2}]}]}""")]
    public async Task AcceptsOptionalScheduleSettings(string json)
    {
        await WriteAsync(json);

        Assert.Equal(0, await ConfigCommand.InvokeAsync(["validate", ConfigPath]));
    }

    [Theory]
    [InlineData("{\"timeZone\":\"Eastern Standard Time\"}")]
    [InlineData("{\"timeZone\":\"Mars/Olympus\"}")]
    [InlineData("{\"schedule\":null}")]
    [InlineData("{\"schedule\":[null]}")]
    [InlineData("{\"schedule\":{}}")]
    [InlineData("""{"schedule":[{"name":"x","days":["funday"],"blocks":[]}]}""")]
    [InlineData("""{"schedule":[{"name":"x","days":["1"],"blocks":[]}]}""")]
    [InlineData("""{"schedule":[{"name":"x","days":[],"blocks":[]}]}""")]
    [InlineData("""{"schedule":[{"name":"x","days":[null],"blocks":[]}]}""")]
    [InlineData("""{"schedule":[{"name":"x","days":["monday"],"blocks":[null]}]}""")]
    [InlineData("""{"schedule":[{"name":"","days":["monday"],"blocks":[]}]}""")]
    [InlineData("""{"schedule":[{"days":["monday"],"blocks":[]}]}""")]
    [InlineData("""{"schedule":[{"name":"x","blocks":[]}]}""")]
    [InlineData("""{"schedule":[{"name":"x","days":["monday"]}]}""")]
    [InlineData("""{"schedule":[{"name":"x","days":["monday"],"days":["friday"],"blocks":[]}]}""")]
    public async Task RejectsInvalidScheduleStructuresAndTimezones(string json)
    {
        await WriteAsync(json);

        Assert.NotEqual(0, await ConfigCommand.InvokeAsync(["validate", ConfigPath]));
    }

    [Theory]
    [InlineData("""{"from":"24:00","to":"4:00","target":23,"range":2}""")]
    [InlineData("""{"from":"14:60","to":"4:00","target":23,"range":2}""")]
    [InlineData("""{"from":"2pm","to":"4:00","target":23,"range":2}""")]
    [InlineData("""{"from":"02:10:00","to":"4:00","target":23,"range":2}""")]
    [InlineData("""{"from":"02:10","to":"02:10","target":23,"range":2}""")]
    [InlineData("""{"from":"02:10","to":"4:00","target":23,"range":0}""")]
    [InlineData("""{"from":"02:10","to":"4:00","target":23}""")]
    [InlineData("""{"from":"02:10","to":"4:00","range":2}""")]
    [InlineData("""{"to":"4:00","target":23,"range":2}""")]
    [InlineData("""{"from":"02:10","target":23,"range":2}""")]
    [InlineData("""{"from":"02:10","to":"4:00","target":23,"target":24,"range":2}""")]
    [InlineData("""{"from":"02:10","to":"4:00","target":23,"range":2,"typo":true}""")]
    public async Task RejectsInvalidScheduleBlocks(string block)
    {
        await WriteAsync("{\"schedule\":[{\"name\":\"x\",\"days\":[\"monday\"],\"blocks\":[" + block + "]}]}");

        Assert.NotEqual(0, await ConfigCommand.InvokeAsync(["validate", ConfigPath]));
    }

    [Fact]
    public async Task RuntimeSettingsLoadFromConfigAndExplicitCliOverridesWin()
    {
        await WriteAsync(
            """
            {"pauseOnGameMode":false,"quitOnFahDisconnect":true,"pauseFahOnExit":false,
             "sensorTimeoutSeconds":30,"fahConnectionTimeoutSeconds":120,
             "allowedTransmissionSources":["serial"],"logFile":"from-config.log"}
            """);
        ControllerOptions? options = null;

        Task Run(ControllerOptions value, CancellationToken _)
        {
            options = value;
            return Task.CompletedTask;
        }

        Assert.Equal(0, await ControllerCommand.InvokeAsync(["--target", "20"], Run, ConfigPath));
        Assert.False(options!.PauseOnGameMode);
        Assert.True(options.QuitOnFahDisconnect);
        Assert.False(options.PauseFahOnExit);
        Assert.Equal(30, options.SensorTimeoutSeconds);
        Assert.Equal(120, options.FahConnectionTimeoutSeconds);
        Assert.Equal(new[] { "serial" }, options.AllowedTransmissionSources);
        Assert.Equal("from-config.log", options.LogFile);

        Assert.Equal(
            0,
            await ControllerCommand.InvokeAsync(
                [
                    "--target",
                    "20",
                    "--pause-on-gamemode",
                    "true",
                    "--quit-on-fah-disconnect",
                    "false",
                    "--pause-fah-on-exit",
                    "true",
                    "--sensor-timeout",
                    "0",
                    "--fah-connection-timeout",
                    "900",
                    "--allowed-transmission-sources",
                    "api",
                    "serial",
                    "--log-file",
                    "from-cli.log"
                ],
                Run,
                ConfigPath));
        Assert.True(options!.PauseOnGameMode);
        Assert.False(options.QuitOnFahDisconnect);
        Assert.True(options.PauseFahOnExit);
        Assert.Equal(0, options.SensorTimeoutSeconds);
        Assert.Equal(900, options.FahConnectionTimeoutSeconds);
        Assert.Equal(new[] { "api", "serial" }, options.AllowedTransmissionSources);
        Assert.Equal("from-cli.log", options.LogFile);
    }

    [Fact]
    public async Task MissingDefaultFileUsesExistingDefaultsWithoutCreatingAnything()
    {
        ControllerOptions? options = null;

        Assert.Equal(
            0,
            await ControllerCommand.InvokeAsync(
                ["--target", "20"],
                (value, _) =>
                {
                    options = value;
                    return Task.CompletedTask;
                },
                ConfigPath));
        Assert.Equal(AppConfig.DefaultSerialPort, options!.PortName);
        Assert.Equal(AppConfig.DefaultSerialBaud, options.BaudRate);
        Assert.Null(options.ApiListenUrl);
        Assert.False(File.Exists(ConfigPath));
    }

    [Fact]
    public async Task LoadsDefaultConfigAndExplicitCliValuesTakePrecedenceEvenWhenEqualToDefaults()
    {
        await WriteAsync(
            """
            {"apiListenUrl":"http://127.0.0.1:6000","fahEndpoint":"wss://example.com:7443/socket",
             "serialPort":"/dev/ttyUSB9","serialBaud":9600}
            """);
        ControllerOptions? options = null;

        Task Run(ControllerOptions value, CancellationToken _)
        {
            options = value;
            return Task.CompletedTask;
        }

        Assert.Equal(0, await ControllerCommand.InvokeAsync(["--target", "20"], Run, ConfigPath));
        Assert.Equal("/dev/ttyUSB9", options!.PortName);
        Assert.Equal(9600, options.BaudRate);
        Assert.Equal("http://127.0.0.1:6000", options.ApiListenUrl);
        Assert.Equal(new Uri("wss://example.com:7443/socket"), options.FahEndpoint);

        Assert.Equal(
            0,
            await ControllerCommand.InvokeAsync(
                [
                    "--target",
                    "20",
                    "--port",
                    AppConfig.DefaultSerialPort,
                    "--baud",
                    "115200",
                    "--fah-endpoint",
                    AppConfig.DefaultFahEndpoint,
                    "--api-listen-url",
                    "http://localhost:5147"
                ],
                Run,
                ConfigPath));
        Assert.Equal(AppConfig.DefaultSerialPort, options!.PortName);
        Assert.Equal(115200, options.BaudRate);
        Assert.Equal(new Uri(AppConfig.DefaultFahEndpoint), options.FahEndpoint);
        Assert.Equal("http://localhost:5147", options.ApiListenUrl);
    }

    [Fact]
    public async Task ExplicitConfigReplacesDefaultFileRatherThanMergingIt()
    {
        await WriteAsync("""{"serialPort":"/dev/default","serialBaud":9600}""");
        string other = Path.Combine(directory, "other.json");

        await File.WriteAllTextAsync(other, """{"serialPort":"/dev/explicit"}""");
        ControllerOptions? options = null;

        Assert.Equal(
            0,
            await ControllerCommand.InvokeAsync(
                ["--target", "20", "--config", other],
                (value, _) =>
                {
                    options = value;
                    return Task.CompletedTask;
                },
                ConfigPath));
        Assert.Equal("/dev/explicit", options!.PortName);
        Assert.Equal(115200, options.BaudRate);
    }

    [Fact]
    public async Task MissingExplicitFileFailsWithoutRunningOrCreatingFile()
    {
        bool ran = false;

        Assert.NotEqual(
            0,
            await ControllerCommand.InvokeAsync(
                ["--target", "20", "--config", ConfigPath],
                (_, _) =>
                {
                    ran = true;
                    return Task.CompletedTask;
                }));
        Assert.False(ran);
        Assert.False(File.Exists(ConfigPath));
        Assert.NotEqual(0, await ConfigCommand.InvokeAsync(["validate", ConfigPath]));
        Assert.False(File.Exists(ConfigPath));
    }

    [Fact]
    public async Task CreateWritesValidTemplateAndNeverOverwritesExistingFile()
    {
        Assert.Equal(
            0,
            await ControllerCommand.InvokeAsync(
                ["config", "create", "--config", ConfigPath],
                (_, _) => throw new InvalidOperationException("Application must not run.")));

        string original = await File.ReadAllTextAsync(ConfigPath);

        Assert.Equal(
            JsonSerializer.Serialize(AppConfig.Template, ConfigJsonContext.Default.AppConfig),
            JsonSerializer.Serialize(
                await ConfigFile.ReadAsync(ConfigPath, CancellationToken.None),
                ConfigJsonContext.Default.AppConfig));
        Assert.Equal(0, await ConfigCommand.InvokeAsync(["validate", ConfigPath]));
        Assert.NotEqual(0, await ConfigCommand.InvokeAsync(["create", ConfigPath]));
        Assert.Equal(original, await File.ReadAllTextAsync(ConfigPath));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"serialBaud\":9600}")]
    [InlineData("{\"apiListenUrl\":\"http://*:5147\"}")]
    [InlineData("{\"apiListenUrl\":\"http://+:5147\"}")]
    [InlineData("{\"apiListenUrl\":\"http://[::]:5147\"}")]
    [InlineData("{\"apiListenUrl\":\"https://localhost:443\"}")]
    [InlineData("{\"fahEndpoint\":\"wss://example.com:7443/api/websocket\"}")]
    public async Task AcceptsPartialConfigurationAndSupportedAddresses(string json)
    {
        await WriteAsync(json);

        Assert.Equal(0, await ConfigCommand.InvokeAsync(["validate", ConfigPath]));
    }

    [Theory]
    [InlineData("{\"sensorTimeoutSeconds\":-1}")]
    [InlineData("{\"fahConnectionTimeoutSeconds\":-1}")]
    [InlineData("{\"pauseOnGameMode\":\"true\"}")]
    [InlineData("{\"allowedTransmissionSources\":[]}")]
    [InlineData("{\"allowedTransmissionSources\":[\"bluetooth\"]}")]
    [InlineData("{\"allowedTransmissionSources\":[null]}")]
    [InlineData("{\"allowedTransmissionSources\":\"api\"}")]
    [InlineData("{\"logFile\":\" \"}")]
    [InlineData("{invalid")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"target\":20}")]
    [InlineData("{\"serialBaud\":\"9600\"}")]
    [InlineData("{\"serialBaud\":0}")]
    [InlineData("{\"serialBaud\":-1}")]
    [InlineData("{\"serialBaud\":1.5}")]
    [InlineData("{\"serialPort\":\" \"}")]
    [InlineData("{\"serialPort\":null}")]
    [InlineData("{\"serialPort\":\"/dev/ttyUSB0\\n\"}")]
    [InlineData("{\"serialBaud\":9600,\"serialBaud\":115200}")]
    [InlineData("{\"fahEndpoint\":\"http://localhost:7396\"}")]
    [InlineData("{\"fahEndpoint\":\"ws://user:password@localhost/socket\"}")]
    [InlineData("{\"fahEndpoint\":\"ws://localhost/socket#fragment\"}")]
    [InlineData("{\"apiListenUrl\":\"localhost:5147\"}")]
    [InlineData("{\"apiListenUrl\":\"http://localhost:0\"}")]
    [InlineData("{\"apiListenUrl\":\"http://localhost:65536\"}")]
    [InlineData("{\"apiListenUrl\":\"http://localhost:5147/sensor\"}")]
    [InlineData("{\"apiListenUrl\":\"http://localhost:5147?query\"}")]
    [InlineData("{\"apiListenUrl\":\"http://user:pass@localhost:5147\"}")]
    public async Task ValidationAndStartupRejectInvalidConfiguration(string json)
    {
        await WriteAsync(json);

        Assert.NotEqual(0, await ConfigCommand.InvokeAsync(["validate", ConfigPath]));

        bool ran = false;

        Assert.NotEqual(
            0,
            await ControllerCommand.InvokeAsync(
                ["--target", "20", "--config", ConfigPath],
                (_, _) =>
                {
                    ran = true;
                    return Task.CompletedTask;
                }));
        Assert.False(ran);
        Assert.Equal(json, await File.ReadAllTextAsync(ConfigPath));
    }

    [Fact]
    public async Task InvalidCliListenerFailsWithoutRunning()
    {
        bool ran = false;

        Assert.NotEqual(
            0,
            await ControllerCommand.InvokeAsync(
                ["--target", "20", "--api-listen-url", "ws://localhost:5147"],
                (_, _) =>
                {
                    ran = true;
                    return Task.CompletedTask;
                },
                ConfigPath));
        Assert.False(ran);
    }

    [Fact]
    public async Task DefaultConfigCommandsUseExecutableLocationAndValidationDoesNotCreateFile()
    {
        Assert.NotEqual(0, await ConfigCommand.InvokeAsync(["validate"], ConfigPath));
        Assert.False(File.Exists(ConfigPath));
        Assert.Equal(0, await ConfigCommand.InvokeAsync(["create"], ConfigPath));
        Assert.Equal(
            JsonSerializer.Serialize(AppConfig.Template, ConfigJsonContext.Default.AppConfig),
            JsonSerializer.Serialize(
                await ConfigFile.ReadAsync(ConfigPath, CancellationToken.None),
                ConfigJsonContext.Default.AppConfig));
        Assert.Equal(0, await ConfigCommand.InvokeAsync(["validate"], ConfigPath));
    }

    [Fact]
    public async Task ConfigCommandRejectsDuplicatePathSelection()
    {
        Assert.NotEqual(0, await ConfigCommand.InvokeAsync(["create", ConfigPath, "--config", ConfigPath]));
        Assert.False(File.Exists(ConfigPath));
    }

    [Fact]
    public async Task MissingExecutableConfigFallsBackToUserConfig()
    {
        string userPath = Path.Combine(directory, "user.json");

        await File.WriteAllTextAsync(userPath, """{"serialBaud":9600}""");
        var (config, path) = await ConfigFile.LoadOptionalAsync(
            null,
            CancellationToken.None,
            ConfigPath,
            userPath);

        Assert.Equal(9600, config.SerialBaud);
        Assert.Equal(userPath, path);
    }

    [Fact]
    public async Task ExecutableConfigTakesPrecedenceWithoutMergingUserConfig()
    {
        await WriteAsync("{}");
        string userPath = Path.Combine(directory, "user.json");

        await File.WriteAllTextAsync(userPath, """{"serialBaud":9600}""");
        var (config, path) = await ConfigFile.LoadOptionalAsync(
            null,
            CancellationToken.None,
            ConfigPath,
            userPath);

        Assert.Null(config.SerialBaud);
        Assert.Equal(ConfigPath, path);
    }

    [Fact]
    public async Task InvalidExecutableConfigDoesNotFallBackToUserConfig()
    {
        await WriteAsync("invalid");
        string userPath = Path.Combine(directory, "user.json");

        await File.WriteAllTextAsync(userPath, "{}");

        await Assert.ThrowsAnyAsync<JsonException>(async () =>
            await ConfigFile.LoadOptionalAsync(
                null,
                CancellationToken.None,
                ConfigPath,
                userPath));
    }

    [Fact]
    public async Task ExplicitMissingConfigDoesNotFallBackToUserConfig()
    {
        string userPath = Path.Combine(directory, "user.json");

        await File.WriteAllTextAsync(userPath, "{}");

        await Assert.ThrowsAsync<FileNotFoundException>(async () =>
            await ConfigFile.LoadOptionalAsync(
                ConfigPath,
                CancellationToken.None,
                ConfigPath,
                userPath));
    }

    [Fact]
    public async Task MissingBothDefaultConfigsUsesDefaultsWithoutCreatingFiles()
    {
        string userPath = Path.Combine(directory, "user.json");

        var (config, path) = await ConfigFile.LoadOptionalAsync(
            null,
            CancellationToken.None,
            ConfigPath,
            userPath);

        Assert.Null(path);
        Assert.Null(config.SerialBaud);
        Assert.False(File.Exists(ConfigPath));
        Assert.False(File.Exists(userPath));
    }

    private async Task WriteAsync(string json) => await File.WriteAllTextAsync(ConfigPath, json);

    public void Dispose() => Directory.Delete(directory, recursive: true);
}