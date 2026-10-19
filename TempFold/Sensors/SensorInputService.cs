// Copyright (c) 2026 smallketchup82 <ketchup@smkt.ca>. Licensed under the MIT License.
// See the LICENSE file in the repository root for full license text.

using System.IO.Ports;
using System.Threading.Channels;
using TempFold.Control;
using TempFold.Models;

namespace TempFold.Sensors;

internal sealed class SensorInputService(
    ControllerOptions options,
    TemperatureController controller,
    ILogger<SensorInputService> logger) : BackgroundService
{
    private readonly Channel<QueuedSensorReading> readings = Channel.CreateBounded<QueuedSensorReading>(
        new BoundedChannelOptions(1)
        {
            SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.DropOldest
        });

    private SerialPort? serial;
    private volatile bool acceptsApiReadings;
    public event Action? DataReceived;

    public bool TryEnqueue(double temperature)
    {
        if (!acceptsApiReadings || !double.IsFinite(temperature)
            || !readings.Writer.TryWrite(new QueuedSensorReading(temperature, "api"))) return false;
        DataReceived?.Invoke();
        return true;
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Target: {Target:F1} °C", options.Target);
        logger.LogInformation("Range: ±{Range:F1} °C", options.Range);
        logger.LogInformation("Start threshold: {Threshold:F1} °C", options.StartThreshold);
        logger.LogInformation("Stop threshold: {Threshold:F1} °C", options.StopThreshold);
        acceptsApiReadings = options.AllowsApi;
        if (options.AllowsApi)
            logger.LogInformation("Waiting for sensor POST requests at /sensor.");
        if (options.AllowsSerial)
        {
            logger.LogInformation("Opening {Port}...", options.PortName);
            try
            {
                serial = new SerialPort(options.PortName, options.BaudRate) { ReadTimeout = 1_000, NewLine = "\n" };
                serial.Open();
                logger.LogInformation("Connected to ESP32 over serial.");
            }
            catch (Exception exception) when (
                exception is UnauthorizedAccessException or IOException or InvalidOperationException)
            {
                DisableSerial(exception);
            }
        }

        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // ReadLine blocks; run it on a worker while the async reader handles both inputs.
        Task serialReader = serial is null ? Task.CompletedTask : Task.Run(() =>
        {
            try
            {
                ReadSerial(stoppingToken);
            }
            catch (Exception exception) when (
                exception is UnauthorizedAccessException or IOException or InvalidOperationException)
            {
                DisableSerial(exception);
            }
        }, CancellationToken.None);
        try
        {
            await foreach (QueuedSensorReading reading in readings.Reader.ReadAllAsync(stoppingToken))
                try
                {
                    controller.ProcessReading(reading.Temperature, reading.Source);
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Controller processing failed");
                }
        }
        finally
        {
            await serialReader;
        }
    }

    private void ReadSerial(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
            try
            {
                string line = serial!.ReadLine().Trim();
                if (SensorReadingParser.TryParseReading(line, out double temperature))
                {
                    if (readings.Writer.TryWrite(new QueuedSensorReading(temperature, "serial")))
                        DataReceived?.Invoke();
                }
            }
            catch (TimeoutException)
            {
                // Check for host shutdown after each read timeout.
            }
    }

    private void DisableSerial(Exception exception)
    {
        serial?.Dispose();
        serial = null;
        logger.LogWarning("Serial unavailable ({Reason}).", exception.Message);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        acceptsApiReadings = false;
        readings.Writer.TryComplete();
        try
        {
            await base.StopAsync(cancellationToken);
        }
        finally
        {
            serial?.Dispose();
            serial = null;
        }
    }

    public override void Dispose()
    {
        base.Dispose();
        serial?.Dispose();
    }
}
