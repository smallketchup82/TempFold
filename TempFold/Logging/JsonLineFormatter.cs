// Copyright (c) 2026 smallketchup82 <ketchup@smkt.ca>. Licensed under the MIT License.
// See the LICENSE file in the repository root for full license text.

using System.Globalization;
using System.Text.Json;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Formatting.Json;

namespace TempFold.Logging;

// Serilog values are written directly, without reflection or arbitrary-object serialization.
internal sealed class JsonLineFormatter : ITextFormatter
{
    private static string Encode(string value) => "\"" + JsonEncodedText.Encode(value).ToString() + "\"";
    private readonly JsonValueFormatter values = new(typeTagName: null);
    public void Format(LogEvent logEvent, TextWriter output)
    {
        output.Write("{\"timestamp\":");
        output.Write(Encode(logEvent.Timestamp.ToString("O", CultureInfo.InvariantCulture)));
        output.Write(",\"level\":");
        output.Write(Encode(logEvent.Level.ToString().ToLowerInvariant()));
        output.Write(",\"message\":");
        output.Write(Encode(logEvent.RenderMessage(CultureInfo.InvariantCulture)));
        if (!logEvent.Properties.ContainsKey("logType")) output.Write(",\"logType\":\"application\"");
        foreach (var property in logEvent.Properties)
        {
            output.Write(',');
            output.Write(Encode(property.Key));
            output.Write(':');
            values.Format(property.Value, output);
        }
        if (logEvent.Exception is { } exception)
        {
            output.Write(",\"exception\":");
            output.Write(Encode(exception.ToString()));
        }
        output.WriteLine("}");
    }
}
