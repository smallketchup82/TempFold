// Copyright (c) 2026 smallketchup82 <ketchup@smkt.ca>. Licensed under the MIT License.
// See the LICENSE file in the repository root for full license text.

using Microsoft.AspNetCore.Http.HttpResults;
using TempFold.Models;
using TempFold.Sensors;

namespace TempFold.Api;

internal static class SensorEndpoints
{
    public static void MapSensorEndpoints(this WebApplication app)
    {
        app.MapPost(
            "/sensor",
            Results<BadRequest<SensorError>, NoContent, StatusCodeHttpResult> (SensorRequest reading, SensorInputService input) =>
            {
                if (reading.Temperature is null)
                    return TypedResults.BadRequest(new SensorError("Temperature is required."));

                return input.TryEnqueue(reading.Temperature.Value)
                    ? TypedResults.NoContent()
                    : TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable);
            });
    }
}
