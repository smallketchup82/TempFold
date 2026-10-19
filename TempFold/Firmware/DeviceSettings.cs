// Copyright (c) 2026 smallketchup82 <ketchup@smkt.ca>. Licensed under the MIT License.
// See the LICENSE file in the repository root for full license text.

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TempFold.Firmware;

internal sealed record DeviceSettings(string BoardName, string WifiSsid = "", string WifiPassword = "", string ApiEndpoint = "");
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(DeviceSettings))]
internal partial class FirmwareJsonContext : JsonSerializerContext;

internal static class FirmwareValidation
{
    internal static bool ValidText(string value, int maximum) => Encoding.UTF8.GetByteCount(value) <= maximum && !value.Any(char.IsControl);
    internal static bool ValidPassword(string value) => value.Length == 0 ||
        (Encoding.UTF8.GetByteCount(value) is >= 8 and <= 63 && !value.Any(char.IsControl)) ||
        (value.Length == 64 && value.All(char.IsAsciiHexDigit));
    internal static bool ValidEndpoint(string value) => value.Length <= 256 &&
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "http" &&
        uri.HostNameType is UriHostNameType.Dns or UriHostNameType.IPv4 && !uri.IsLoopback &&
        uri.Host.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-') &&
        uri.Host != "0.0.0.0" && uri.Port is > 0 and <= 65535 && uri.AbsolutePath == "/" &&
        uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0 &&
        !value.EndsWith('/') && !value.Any(char.IsWhiteSpace);

    internal static JsonDocument? ParseResponse(string line, string command)
    {
        if (!line.StartsWith("TF=", StringComparison.Ordinal)) return null;
        JsonDocument doc = JsonDocument.Parse(line.AsMemory(3));
        if (doc.RootElement.GetProperty("command").GetString() != command) { doc.Dispose(); return null; }
        if (!doc.RootElement.GetProperty("ok").GetBoolean())
        {
            string error = doc.RootElement.GetProperty("error").GetString() ?? "UNKNOWN";
            doc.Dispose();
            throw new InvalidDataException($"The board rejected {command}: {error}. Check that this is the latest TFirm firmware.");
        }
        return doc;
    }
}
