// Copyright (c) 2026 smallketchup82 <ketchup@smkt.ca>. Licensed under the MIT License.
// See the LICENSE file in the repository root for full license text.

using System.Text.Json;

namespace TempFold.Firmware;

// GET never returns the password. A null patch value means preserve the secret.
internal sealed record FirmwareConfiguration(string BoardName, string WifiSsid, string ApiEndpoint,
    int SensorIntervalMs, int SdaPin, int SclPin, int SensorAddress, bool WifiPasswordSet)
{
    internal static FirmwareConfiguration Read(JsonElement status)
    {
        FirmwareInstaller.ValidateDevice(status, null);
        if (!status.TryGetProperty("configPatch", out var patch) || !patch.GetBoolean())
            throw new InvalidDataException("This TFirm lacks safe configuration updates. Install the latest firmware with firmware setup first.");
        var settings = new FirmwareConfiguration(status.GetProperty("boardName").GetString()!, status.GetProperty("wifiSsid").GetString()!,
            status.GetProperty("apiEndpoint").GetString()!, status.GetProperty("sensorIntervalMs").GetInt32(),
            status.GetProperty("sdaPin").GetInt32(), status.GetProperty("sclPin").GetInt32(),
            status.GetProperty("sensorAddress").GetInt32(), status.GetProperty("wifiPasswordSet").GetBoolean());
        settings.Validate(null);
        return settings;
    }

    internal void Validate(string? password)
    {
        if (string.IsNullOrWhiteSpace(BoardName) || !FirmwareValidation.ValidText(BoardName, 64) ||
            !FirmwareValidation.ValidText(WifiSsid, 32) ||
            (ApiEndpoint.Length > 0 && (WifiSsid.Length == 0 || !FirmwareValidation.ValidEndpoint(ApiEndpoint))) ||
            (password is not null && !FirmwareValidation.ValidPassword(password)) ||
            SensorIntervalMs is < 1000 or > 3600000 || !ValidPin(SdaPin) || !ValidPin(SclPin) || SdaPin == SclPin || SensorAddress is not (68 or 69))
            throw new InvalidDataException("Invalid firmware settings. Check name, Wi-Fi, HTTP server, interval (1000–3600000 ms), and sensor wiring.");
    }

    private static bool ValidPin(int pin) => pin is 0 or 1 or >= 3 and <= 10 or 20 or 21;

    internal string Patch(FirmwareConfiguration saved, string? password)
    {
        Validate(password);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            if (BoardName != saved.BoardName) writer.WriteString("boardName", BoardName);
            if (WifiSsid != saved.WifiSsid) writer.WriteString("wifiSsid", WifiSsid);
            if (ApiEndpoint != saved.ApiEndpoint) writer.WriteString("apiEndpoint", ApiEndpoint);
            if (password is not null) writer.WriteString("wifiPassword", password);
            if (SensorIntervalMs != saved.SensorIntervalMs) writer.WriteNumber("sensorIntervalMs", SensorIntervalMs);
            if (SdaPin != saved.SdaPin) writer.WriteNumber("sdaPin", SdaPin);
            if (SclPin != saved.SclPin) writer.WriteNumber("sclPin", SclPin);
            if (SensorAddress != saved.SensorAddress) writer.WriteNumber("sensorAddress", SensorAddress);
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    internal void Verify(FirmwareConfiguration actual, string? password)
    {
        bool expectedPassword = password is null ? WifiPasswordSet : password.Length > 0;
        if (this with { WifiPasswordSet = expectedPassword } != actual)
            throw new InvalidDataException("Configuration readback differs from the requested settings after restart.");
    }
}
