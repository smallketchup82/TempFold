// Copyright (c) 2026 smallketchup82 <ketchup@smkt.ca>. Licensed under the MIT License.
// See the LICENSE file in the repository root for full license text.

using System.Text.Json;
using TempFold.Api;
using TempFold.Models;

namespace TempFold.Tests;

public sealed class SensorJsonTests
{
    [Fact]
    public void DeserializesWebPropertyNamesWithoutReflection()
    {
        SensorRequest? reading = JsonSerializer.Deserialize(
            """{"temperature":22.5}""",
            SensorJsonContext.Default.SensorRequest);

        Assert.Equal(new SensorRequest(22.5), reading);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"temperature\":null}")]
    public void MissingOrNullValuesRemainNull(string json) =>
        Assert.Equal(
            new SensorRequest(null),
            JsonSerializer.Deserialize(json, SensorJsonContext.Default.SensorRequest));

    [Theory]
    [InlineData("{invalid")]
    [InlineData("{\"temperature\":\"hot\"}")]
    public void RejectsInvalidJsonOrNonNumericValues(string json) =>
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(json, SensorJsonContext.Default.SensorRequest));

    [Fact]
    public void SerializesErrorUsingWebPropertyNames() =>
        Assert.Equal(
            """{"error":"Required"}""",
            JsonSerializer.Serialize(new SensorError("Required"), SensorJsonContext.Default.SensorError));
}