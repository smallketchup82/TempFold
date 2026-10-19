// Copyright (c) 2026 smallketchup82 <ketchup@smkt.ca>. Licensed under the MIT License.
// See the LICENSE file in the repository root for full license text.

using System.Globalization;
using TempFold.Sensors;

namespace TempFold.Tests;

public sealed class SensorReadingParserTests
{
    [Theory]
    [InlineData("TEMP=22.5", 22.5)]
    [InlineData("TEMP=-10", -10)]
    [InlineData("TEMP=+2.5e1", 25)]
    [InlineData("TEMP= 22.5 ", 22.5)]
    public void ParsesInvariantNumbers(string line, double expectedTemperature)
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");

            Assert.True(SensorReadingParser.TryParseReading(line, out double temperature));
            Assert.Equal(expectedTemperature, temperature);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("temp=22")]
    [InlineData(" TEMP=22")]
    [InlineData("TEMP=22,EXTRA=1")]
    [InlineData("TEMP=abc")]
    [InlineData("TEMP=")]
    [InlineData("TEMP=22,5")]
    public void RejectsMalformedReadings(string line) => Assert.False(SensorReadingParser.TryParseReading(line, out _));
}