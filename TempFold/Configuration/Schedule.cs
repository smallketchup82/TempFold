// Copyright (c) 2026 smallketchup82 <ketchup@smkt.ca>. Licensed under the MIT License.
// See the LICENSE file in the repository root for full license text.

using System.Globalization;
using System.Text.Json.Serialization;

namespace TempFold.Configuration;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record Schedule(
    [property: JsonRequired] string Name,
    [property: JsonRequired] string[] Days,
    [property: JsonRequired] ScheduleBlock[] Blocks)
{
    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name) || Name.Any(char.IsControl))
            throw new InvalidDataException("Each schedule needs a nonempty name without control characters.");
        if (Days is null || Days.Length == 0 || Days.Any(day => !TryParseDay(day, out _)))
            throw new InvalidDataException($"Schedule {Name}: days must contain weekday names such as monday.");
        if (Blocks is null)
            throw new InvalidDataException($"Schedule {Name}: blocks must be an array.");
        foreach (ScheduleBlock block in Blocks)
        {
            if (block is null)
                throw new InvalidDataException($"Schedule {Name}: blocks cannot contain null entries.");
            block.Validate();
        }
    }

    internal static bool TryParseDay(string? day, out DayOfWeek result) =>
        Enum.TryParse(day, ignoreCase: true, out result) && Enum.IsDefined(result)
        && string.Equals(day, result.ToString(), StringComparison.OrdinalIgnoreCase);

    internal bool Includes(DayOfWeek day) => Days.Any(value =>
        string.Equals(value, day.ToString(), StringComparison.OrdinalIgnoreCase));
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ScheduleBlock(
    [property: JsonRequired] string From,
    [property: JsonRequired] string To,
    [property: JsonRequired] double Target,
    [property: JsonRequired] double Range,
    bool Enabled = true)
{
    internal static bool TryParseTime(string? value, out TimeOnly time) =>
        TimeOnly.TryParseExact(value, ["HH:mm", "H:mm"], CultureInfo.InvariantCulture,
            DateTimeStyles.None, out time);

    internal TimeOnly Start => TimeOnly.ParseExact(From, ["HH:mm", "H:mm"], CultureInfo.InvariantCulture);
    internal TimeOnly End => TimeOnly.ParseExact(To, ["HH:mm", "H:mm"], CultureInfo.InvariantCulture);

    internal void Validate()
    {
        if (!TryParseTime(From, out TimeOnly start) || !TryParseTime(To, out TimeOnly end) || start == end)
            throw new InvalidDataException("Schedule blocks need distinct from/to times in 24-hour H:mm or HH:mm format (00:00–23:59).");
        if (!double.IsFinite(Target) || !double.IsFinite(Range) || Range <= 0)
            throw new InvalidDataException("Schedule block target must be finite and range must be finite and greater than 0.");
    }
}
