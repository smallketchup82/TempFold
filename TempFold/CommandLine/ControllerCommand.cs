// Copyright (c) 2026 smallketchup82 <ketchup@smkt.ca>. Licensed under the MIT License.
// See the LICENSE file in the repository root for full license text.

using System.CommandLine;
using System.Text.Json;
using TempFold.Configuration;
using TempFold.Control;
using TempFold.Logging;

namespace TempFold.CommandLine;

internal static class ControllerCommand
{
    public static Task<int> InvokeAsync(string[] args, Func<ControllerOptions, CancellationToken, Task> run, string? defaultConfigPath = null)
    {
        if (args.FirstOrDefault() == "firmware")
            return FirmwareCommand.InvokeAsync(args[1..], defaultConfigPath);
        if (args.FirstOrDefault() == "config")
            return ConfigCommand.InvokeAsync(args[1..], defaultConfigPath);
        if (args.FirstOrDefault() == "fah")
            return FahCommand.InvokeAsync(args[1..], defaultConfigPath);
        var rootCommand = new RootCommand("Folding@home as a thermostat.");
        Configure(rootCommand, run, defaultConfigPath: defaultConfigPath);
        rootCommand.Subcommands.Add(new Command("config", "Create or validate a configuration file."));
        rootCommand.Subcommands.Add(new Command("firmware", "Install and configure ESP32-C3 firmware."));
        rootCommand.Subcommands.Add(new Command("fah", "Start, stop, or inspect Folding@home and exit."));
        return rootCommand.Parse(args).InvokeAsync();
    }

    internal static void Configure(Command rootCommand, Func<ControllerOptions, CancellationToken, Task> run,
        string? defaultConfigPath = null)
    {
        var targetOption = new Option<double>("--target")
        {
            Description = "Target room temperature in Celsius.", Required = true
        };

        var rangeOption = new Option<double>("--range")
        {
            Description = "Temperature range around the target in Celsius.", DefaultValueFactory = _ => 3.0
        };

        var portOption = new Option<string>("--port")
        {
            Description = "Serial port used by the ESP32.", DefaultValueFactory = _ => AppConfig.DefaultSerialPort
        };

        var baudOption = new Option<int>("--baud")
        {
            Description = "Serial baud rate used by the ESP32.", DefaultValueFactory = _ => AppConfig.DefaultSerialBaud
        };

        var endpointOption = new Option<string>("--fah-endpoint")
        {
            Description = "Folding@home v8 WebSocket endpoint (ws:// or wss://).",
            DefaultValueFactory = _ => AppConfig.DefaultFahEndpoint
        };

        var configOption = new Option<string?>("--config")
        {
            Description = "Configuration file path; otherwise search beside the executable, then ~/.config/tempfold/config.json."
        };
        var listenOption = new Option<string?>("--api-listen-url")
        {
            Description = "API listening URL including port (http:// or https://); overrides configuration and ASPNETCORE_URLS."
        };
        var pauseGameModeOption = new Option<bool>("--pause-on-gamemode")
        {
            Description = "Pause folding while GameMode is active; use false to disable.", DefaultValueFactory = _ => true
        };
        var quitDisconnectOption = new Option<bool>("--quit-on-fah-disconnect")
        {
            Description = "Quit when an established FAH connection disconnects (default: false)."
        };
        var pauseExitOption = new Option<bool>("--pause-fah-on-exit")
        {
            Description = "Pause FAH on application exit; use false to disable.", DefaultValueFactory = _ => true
        };
        var sensorTimeoutOption = new Option<double>("--sensor-timeout")
        {
            Description = "Quit after this many seconds without valid ESP32 data; 0 disables the timeout (default: 0)."
        };
        var fahTimeoutOption = new Option<double>("--fah-connection-timeout")
        {
            Description = "Quit after this many seconds without a FAH connection; 0 disables the timeout.",
            DefaultValueFactory = _ => AppConfig.DefaultFahConnectionTimeoutSeconds
        };
        var sourcesOption = new Option<string[]>("--allowed-transmission-sources")
        {
            Description = "Allowed sensor inputs: api and/or serial (default: both).",
            Arity = ArgumentArity.OneOrMore, AllowMultipleArgumentsPerToken = true
        };
        var logFileOption = new Option<string?>("--log-file")
        {
            Description = "Write rotating JSON Lines logs to this file; file logging is opt-in."
        };
        var logDirectoryOption = new Option<string?>("--log-directory") { Description = "Rotating JSON Lines directory; use default for the platform state/log directory." };
        var verbosityOption = new Option<string?>("--verbosity") { Description = "Console minimum level: verbose, debug, information, warning, error, fatal (default: information)." };
        var logLevelOption = new Option<string?>("--log-level") { Description = "File minimum level (default: verbose)." };
        var logSizeOption = new Option<long?>("--log-max-size") { Description = "Maximum bytes per log file (default: 20971520)." };
        var logRetentionOption = new Option<int?>("--log-retained-files") { Description = "Maximum retained log files (default: 31)." };
        var noScheduleOption = new Option<bool>("--no-schedule")
        {
            Description = "Ignore schedules from the configuration file and use the CLI target and range."
        };
        var pausedOption = new Option<bool>("--paused")
        {
            Description = "Start with automatic temperature control manually paused; press p to resume."
        };

        var nonInteractiveOption = new Option<bool>("--non-interactive")
        {
            Description = "Print each status update on a new line.",
            DefaultValueFactory = _ => false
        };
        var noColorOption = new Option<bool>("--no-color") { Description = "Disable colors in status output and logs." };
        var effectsOption = new Option<bool>("--effects") { Description = "Enable sensor flashes in interactive colored output (off by default)." };
        var intervalOption = new Option<double?>("--status-interval")
        {
            Description = "Seconds between non-interactive status prints; requires --non-interactive or --json. Defaults: text 1, JSON 5."
        };
        var jsonOption = new Option<bool>("--json") { Description = "Print one JSON status object per line, every 5 seconds by default; logs go to stderr." };

        rootCommand.Options.Add(configOption);
        rootCommand.Options.Add(listenOption);
        rootCommand.Options.Add(targetOption);
        rootCommand.Options.Add(rangeOption);
        rootCommand.Options.Add(portOption);
        rootCommand.Options.Add(baudOption);
        rootCommand.Options.Add(endpointOption);
        rootCommand.Options.Add(nonInteractiveOption);
        rootCommand.Options.Add(noColorOption);
        rootCommand.Options.Add(effectsOption);
        rootCommand.Options.Add(pauseGameModeOption);
        rootCommand.Options.Add(quitDisconnectOption);
        rootCommand.Options.Add(pauseExitOption);
        rootCommand.Options.Add(sensorTimeoutOption);
        rootCommand.Options.Add(fahTimeoutOption);
        rootCommand.Options.Add(sourcesOption);
        rootCommand.Options.Add(logFileOption);
        foreach (Option option in new Option[] { logDirectoryOption, verbosityOption, logLevelOption, logSizeOption, logRetentionOption }) rootCommand.Options.Add(option);
        rootCommand.Options.Add(noScheduleOption);
        rootCommand.Options.Add(pausedOption);
        rootCommand.Options.Add(intervalOption);
        rootCommand.Options.Add(jsonOption);

        rootCommand.SetAction(async (parseResult, cancellationToken) =>
        {
            double target = parseResult.GetValue(targetOption);
            double range = parseResult.GetValue(rangeOption);
            AppConfig config;
            try
            {
                (config, _) = await ConfigFile.LoadOptionalAsync(
                    parseResult.GetValue(configOption), cancellationToken, defaultConfigPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
            {
                Console.Error.WriteLine($"Configuration error: {ex.Message}");
                return 1;
            }
            string port = parseResult.GetResult(portOption) is { Implicit: false }
                ? parseResult.GetValue(portOption)! : config.SerialPort ?? AppConfig.DefaultSerialPort;
            int baud = parseResult.GetResult(baudOption) is { Implicit: false }
                ? parseResult.GetValue(baudOption) : config.SerialBaud ?? AppConfig.DefaultSerialBaud;
            string endpointText = parseResult.GetResult(endpointOption) is { Implicit: false }
                ? parseResult.GetValue(endpointOption)! : config.FahEndpoint ?? AppConfig.DefaultFahEndpoint;
            string? listenUrl = parseResult.GetValue(listenOption) ?? config.ApiListenUrl;
            bool pauseGameMode = parseResult.GetResult(pauseGameModeOption) is { Implicit: false }
                ? parseResult.GetValue(pauseGameModeOption) : config.PauseOnGameMode ?? true;
            bool quitDisconnect = parseResult.GetResult(quitDisconnectOption) is { Implicit: false }
                ? parseResult.GetValue(quitDisconnectOption) : config.QuitOnFahDisconnect ?? false;
            bool pauseExit = parseResult.GetResult(pauseExitOption) is { Implicit: false }
                ? parseResult.GetValue(pauseExitOption) : config.PauseFahOnExit ?? true;
            double sensorTimeout = parseResult.GetResult(sensorTimeoutOption) is { Implicit: false }
                ? parseResult.GetValue(sensorTimeoutOption) : config.SensorTimeoutSeconds ?? 0;
            double fahTimeout = parseResult.GetResult(fahTimeoutOption) is { Implicit: false }
                ? parseResult.GetValue(fahTimeoutOption) : config.FahConnectionTimeoutSeconds ?? AppConfig.DefaultFahConnectionTimeoutSeconds;
            string[] sources = parseResult.GetResult(sourcesOption) is { Implicit: false }
                ? parseResult.GetValue(sourcesOption)! : config.AllowedTransmissionSources ?? AppConfig.DefaultTransmissionSources;
            string? logFile = parseResult.GetValue(logFileOption) ?? config.LogFile;
            string? logDirectory = parseResult.GetValue(logDirectoryOption) ?? config.LogDirectory;
            string verbosity = parseResult.GetValue(verbosityOption) ?? config.Verbosity ?? "information";
            string logLevel = parseResult.GetValue(logLevelOption) ?? config.LogLevel ?? "verbose";
            long logSize = parseResult.GetValue(logSizeOption) ?? config.LogMaxSizeBytes ?? 20 * 1024 * 1024;
            int logRetention = parseResult.GetValue(logRetentionOption) ?? config.LogRetainedFiles ?? 31;
            if ((logDirectory is not null && !AppConfig.IsLogFile(logDirectory)) || (logFile is not null && logDirectory is not null)
                || !FileLogging.TryLevel(verbosity, out _) || !FileLogging.TryLevel(logLevel, out _) || logSize <= 0 || logRetention <= 0)
            {
                Console.Error.WriteLine("Invalid logging options: choose a file or directory, valid levels, and positive size/retention.");
                return 1;
            }
            if (!AppConfig.IsTimeout(sensorTimeout) || !AppConfig.IsTimeout(fahTimeout))
            {
                Console.Error.WriteLine("--sensor-timeout and --fah-connection-timeout must be finite, nonnegative seconds below TimeSpan.MaxValue (0 disables the timeout).");
                return 1;
            }
            if (!AppConfig.AreTransmissionSources(sources))
            {
                Console.Error.WriteLine("--allowed-transmission-sources requires api and/or serial.");
                return 1;
            }
            if (logFile is not null && !AppConfig.IsLogFile(logFile))
            {
                Console.Error.WriteLine("--log-file must be a nonempty file path without control characters.");
                return 1;
            }

            if (!double.IsFinite(target) || !double.IsFinite(range) || range <= 0)
            {
                Console.Error.WriteLine("--target must be finite and --range must be finite and greater than 0.");
                return 1;
            }

            bool json = parseResult.GetValue(jsonOption);
            bool nonInteractive = parseResult.GetValue(nonInteractiveOption) || json;
            double? requestedInterval = parseResult.GetValue(intervalOption);
            double interval = requestedInterval ?? (json ? 5 : 1);
            if (requestedInterval is not null && !nonInteractive)
            {
                Console.Error.WriteLine("--status-interval requires --non-interactive or --json.");
                return 1;
            }
            // Timers use millisecond intervals; reject values they cannot represent.
            if (!double.IsFinite(interval) || interval < 0.001 || interval * 1000 > uint.MaxValue - 1)
            {
                Console.Error.WriteLine("--status-interval must be a finite positive timer duration (at least 0.001 seconds).");
                return 1;
            }

            if (string.IsNullOrWhiteSpace(port) || port.Any(char.IsControl))
            {
                Console.Error.WriteLine("--port must not be empty.");
                return 1;
            }

            if (baud <= 0)
            {
                Console.Error.WriteLine("--baud must be greater than 0.");
                return 1;
            }

            if (!AppConfig.IsFahEndpoint(endpointText))
            {
                Console.Error.WriteLine("--fah-endpoint must be an absolute ws:// or wss:// URL without credentials or a fragment.");
                return 1;
            }
            if (listenUrl is not null && !AppConfig.IsListenUrl(listenUrl))
            {
                Console.Error.WriteLine("--api-listen-url must be an http:// or https:// listening URL with a valid host and port, without credentials, path, query, or fragment.");
                return 1;
            }

            await run(
                new ControllerOptions(
                    target,
                    range,
                    port,
                    baud,
                    new Uri(endpointText))
                {
                    ApiListenUrl = listenUrl,
                    PauseOnGameMode = pauseGameMode,
                    QuitOnFahDisconnect = quitDisconnect,
                    PauseFahOnExit = pauseExit,
                    SensorTimeoutSeconds = sensorTimeout,
                    FahConnectionTimeoutSeconds = fahTimeout,
                    AllowedTransmissionSources = sources,
                    LogFile = logFile, LogDirectory = logDirectory, Verbosity = verbosity,
                    LogLevel = logLevel, LogMaxSizeBytes = logSize, LogRetainedFiles = logRetention,
                    Schedule = config.Schedule,
                    TimeZone = config.TimeZone is { } timeZone ? AppConfig.ResolveTimeZone(timeZone) : null,
                    NoSchedule = parseResult.GetValue(noScheduleOption),
                    StartPaused = parseResult.GetValue(pausedOption),
                    Status = new StatusOutputOptions(
                        Interactive: !nonInteractive,
                        Colors: !parseResult.GetValue(noColorOption),
                        Effects: parseResult.GetValue(effectsOption),
                        Json: json, IntervalSeconds: interval)
                },
                cancellationToken);

            return 0;
        });

    }
}
