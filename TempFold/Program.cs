// Copyright (c) 2026 smallketchup82 <ketchup@smkt.ca>. Licensed under the MIT License.
// See the LICENSE file in the repository root for full license text.

using Serilog;
using Serilog.Events;
using TempFold.Api;
using TempFold.CommandLine;
using TempFold.Control;
using TempFold.Integrations;
using TempFold.Logging;
using TempFold.Sensors;

namespace TempFold;

internal static class Program
{
    private static Task<int> Main(string[] args) => ControllerCommand.InvokeAsync(args, RunAsync);

    private static async Task RunAsync(ControllerOptions options, CancellationToken cancellationToken)
    {
        using var console = new StatusConsole(options.Status);
        IHostApplicationBuilder builder = options.AllowsApi
            ? WebApplication.CreateSlimBuilder() : Host.CreateApplicationBuilder();
        FileLogging.TryLevel(options.Verbosity, out var consoleLevel);
        LoggerConfiguration loggerConfiguration = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("sessionId", Guid.NewGuid().ToString("N"))
            .WriteTo.Sink(console, restrictedToMinimumLevel: consoleLevel);
        FileLogging.Configure(loggerConfiguration, options.LogFile, options.LogDirectory, options.LogLevel,
            options.LogMaxSizeBytes, options.LogRetainedFiles);
        using var logger = loggerConfiguration.CreateLogger();
        logger.Information("{logType}: TempFold starting with target {target}, range {range}, log file {logFile}, log directory {logDirectory}, initially paused {paused}",
            "startup", options.Target, options.Range, options.LogFile,
            options.LogDirectory == "default" ? FileLogging.DefaultDirectory() : options.LogDirectory, options.StartPaused);
        builder.Services.AddSingleton(console);
        builder.Logging.ClearProviders();
        builder.Services.AddSerilog(logger);
        if (builder is WebApplicationBuilder webBuilder)
        {
            webBuilder.WebHost.UseKestrelHttpsConfiguration();
            if (options.ApiListenUrl is not null)
                webBuilder.WebHost.UseUrls(options.ApiListenUrl);
            builder.Services.ConfigureHttpJsonOptions(jsonOptions =>
                jsonOptions.SerializerOptions.TypeInfoResolverChain.Insert(0, SensorJsonContext.Default));
        }
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<GameModeClient>();
        builder.Services.AddSingleton<FahService>();
        // Subscribe to connection changes before either input service starts.
        builder.Services.AddHostedService<RuntimeLifetimeService>();
        builder.Services.AddHostedService(provider => provider.GetRequiredService<FahService>());
        builder.Services.AddSingleton<TemperatureController>();
        builder.Services.AddHostedService<ScheduleService>();
        builder.Services.AddSingleton<SensorInputService>();
        builder.Services.AddHostedService(provider => provider.GetRequiredService<SensorInputService>());
        builder.Services.AddHostedService<ControllerStatusService>();
        builder.Services.AddHostedService<InteractiveInputService>();

        try
        {
            if (builder is WebApplicationBuilder apiBuilder)
            {
                await using WebApplication app = apiBuilder.Build();
                app.MapSensorEndpoints();
                await app.RunAsync(cancellationToken);
            }
            else
            {
                using IHost host = ((HostApplicationBuilder)builder).Build();
                await host.RunAsync(cancellationToken);
            }
        }
        finally
        {
            logger.Information("{logType}: TempFold stopped", "shutdown");
        }
    }
}
