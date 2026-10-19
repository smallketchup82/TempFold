// Copyright (c) 2026 smallketchup82 <ketchup@smkt.ca>. Licensed under the MIT License.
// See the LICENSE file in the repository root for full license text.

using System.Globalization;

namespace TempFold.Sensors;

internal static class SensorReadingParser
{
    public static bool TryParseReading(string line, out double temperature)
    {
        temperature = 0;
        return line.StartsWith("TEMP=", StringComparison.Ordinal)
            && double.TryParse(line["TEMP=".Length..], NumberStyles.Float,
                CultureInfo.InvariantCulture, out temperature);
    }
}
