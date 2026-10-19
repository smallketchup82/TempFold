// Copyright (c) 2026 smallketchup82 <ketchup@smkt.ca>. Licensed under the MIT License.
// See the LICENSE file in the repository root for full license text.

using System.Text.Json;
using System.Text.Json.Nodes;
using TempFold.Models;

namespace TempFold.Integrations;

// v8 sends a snapshot followed by path/value arrays, rather than JSON Patch.
internal sealed class FahState
{
    private JsonObject state = [];

    public void Apply(JsonNode? message, bool initial = false)
    {
        if (initial)
        {
            state = message as JsonObject ?? throw new JsonException("Expected a client state object.");

            if (Status is null)
                throw new JsonException("Client state contains no resource group configuration.");

            return;
        }

        if (message is not JsonArray patch)
            return; // Includes the client's application-level "ping" keep-alive.

        if (patch.Count < 2)
            throw new JsonException("Invalid state update.");

        JsonNode current = state;

        for (int i = 0; i < patch.Count - 2; i++)
        {
            JsonNode? child = getNode(current, patch[i]!);

            if (child is null)
            {
                child = patch[i + 1] is JsonValue next && next.TryGetValue<int>(out _)
                    ? new JsonArray()
                    : new JsonObject();

                setNode(current, patch[i]!, child);
            }

            current = child;
        }

        setNode(current, patch[^2]!, patch[^1]?.DeepClone());
    }

    private static JsonNode? getNode(JsonNode parent, JsonNode key) => parent switch
    {
        JsonObject obj => obj[key.GetValue<string>()],
        JsonArray array when key.GetValue<int>() >= 0 && key.GetValue<int>() < array.Count
            => array[key.GetValue<int>()],
        JsonArray => null,
        _ => throw new JsonException("Invalid update path.")
    };

    private static void setNode(JsonNode parent, JsonNode key, JsonNode? value)
    {
        if (parent is JsonObject obj)
        {
            string name = key.GetValue<string>();

            if (value is null)
                obj.Remove(name);
            else
                obj[name] = value;
        }
        else if (parent is JsonArray array)
        {
            int index = key.GetValue<int>();

            if (index == -1)
                array.Add(value);
            else if (index == -2 && value is JsonArray values)
                foreach (JsonNode? item in values)
                    array.Add(item?.DeepClone());
            else if (index >= 0)
            {
                if (value is null)
                {
                    if (index < array.Count)
                        array.RemoveAt(index);
                }
                else
                {
                    if (index > 100_000)
                        throw new JsonException("Array index exceeds limit.");

                    while (array.Count <= index)
                        array.Add(null);

                    array[index] = value;
                }
            }
            else
                throw new JsonException("Invalid array update.");
        }
        else
            throw new JsonException("Invalid update container.");
    }

    private Dictionary<string, JsonObject> configurations
    {
        get
        {
            var result = new Dictionary<string, JsonObject>();

            if (state["groups"] is JsonObject groups)
            {
                foreach (KeyValuePair<string, JsonNode?> group in groups)
                    if (group.Value?["config"] is JsonObject config)
                        result[group.Key] = config;
            }
            else if (state["config"] is JsonObject config)
            {
                // Older v8 clients keep the default group's configuration at the root.
                result[""] = config;
            }

            return result;
        }
    }

    private static bool getFlag(JsonNode? config, string key) => config?[key]?.GetValue<bool>() ?? false;

    private static bool isFolding(JsonObject config) => !getFlag(config, "paused") && !getFlag(config, "finish");

    private static bool isUnitWorking(JsonNode? unit, Dictionary<string, JsonObject> groups)
    {
        if (unit is not JsonObject || unit["state"]?.GetValue<string>() != "RUN" || getFlag(unit, "paused"))
            return false;

        string groupName = unit["group"]?.GetValue<string>() ?? "";

        return groups.TryGetValue(groupName, out JsonObject? config) && isFolding(config);
    }

    public bool Matches(bool folding)
    {
        Dictionary<string, JsonObject> groups = configurations;

        return groups.Count > 0 && groups.Values.All(
            config => folding ? isFolding(config) : getFlag(config, "paused"));
    }

    public FoldingStatus? Status
    {
        get
        {
            Dictionary<string, JsonObject> groups = configurations;

            if (groups.Count == 0)
                return null;

            bool working = state["units"] is JsonArray units && units.Any(unit => isUnitWorking(unit, groups));

            return new FoldingStatus(groups.Values.Any(isFolding), working);
        }
    }
}