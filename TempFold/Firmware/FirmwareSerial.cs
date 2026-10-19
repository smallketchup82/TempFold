// Copyright (c) 2026 smallketchup82 <ketchup@smkt.ca>. Licensed under the MIT License.
// See the LICENSE file in the repository root for full license text.

using System.Globalization;
using System.IO.Ports;
using System.Text;
using System.Text.Json;

namespace TempFold.Firmware;

internal sealed class FirmwareSerial : IDisposable
{
    private readonly SerialPort port;
    internal FirmwareSerial(string address)
    {
        port = new SerialPort(address, 115200) { NewLine = "\n", Encoding = Encoding.UTF8, ReadTimeout = 250, WriteTimeout = 2000 };
        try { port.Open(); } catch { port.Dispose(); throw; }
    }

    internal Task<JsonDocument> RequestAsync(string command, string payload, CancellationToken token) => Task.Run(() =>
    {
        // Never write payloads/passwords to logs or pass them as process arguments.
        port.WriteLine("TF " + command + payload);
        long deadline = Environment.TickCount64 + 15000;
        while (Environment.TickCount64 < deadline)
        {
            token.ThrowIfCancellationRequested();
            string? line = ReadLine(token);
            if (line is null) continue;
            JsonDocument? response = FirmwareValidation.ParseResponse(line, command);
            if (response is not null) return response;
        }
        throw new IOException($"The board did not answer {command}. Check the serial port and USB CDC firmware build setting.");
    }, token);

    internal Task<double> TemperatureAsync(CancellationToken token, TimeSpan? timeout = null) => Task.Run(() =>
    {
        long deadline = Environment.TickCount64 + (long)(timeout ?? TimeSpan.FromSeconds(15)).TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            token.ThrowIfCancellationRequested();
            string? line = ReadLine(token);
            if (line is not null && line.StartsWith("TEMP=", StringComparison.Ordinal) &&
                double.TryParse(line.AsSpan(5), NumberStyles.Float, CultureInfo.InvariantCulture, out double temperature) && double.IsFinite(temperature))
                return temperature;
        }
        throw new IOException("Firmware is configured, but no temperature reading arrived. Check sensor wiring, GPIO8/9, and address 0x44.");
    }, token);

    // Accumulate bytes ourselves: SerialPort.ReadLine loses partial data when its
    // timeout expires. Bound lines so unrelated boot diagnostics cannot exhaust RAM.
    private readonly StringBuilder lineBuffer = new();
    private string? ReadLine(CancellationToken token)
    {
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                int c = port.ReadChar();
                if (c == '\n') { string result = lineBuffer.ToString(); lineBuffer.Clear(); return result; }
                if (c != '\r') lineBuffer.Append((char)c);
                if (lineBuffer.Length > 4096) throw new InvalidDataException("The board emitted an oversized serial response.");
            }
        }
        catch (TimeoutException) { return null; }
    }
    public void Dispose() => port.Dispose();
}
