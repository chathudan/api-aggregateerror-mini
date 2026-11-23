using System.Text.Json.Nodes;

namespace Aggregator.Api.Aggregation;

/// <summary>
/// Pure, side-effect free JSON helpers: select, project, combine, merge, de-duplicate.
/// Every method returns new nodes (never re-parents its inputs), so callers can freely
/// attach the results to another document.
/// </summary>
public static class JsonShaping
{
    /// <summary>Walks a dot path ("data.items", "items.0.id"). Returns null if any segment is missing.</summary>
    public static JsonNode? Select(JsonNode? node, string? path)
    {
        if (node is null || string.IsNullOrWhiteSpace(path)) return node;

        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            node = node switch
            {
                JsonObject obj => obj[segment],
                JsonArray arr when int.TryParse(segment, out var i) && i >= 0 && i < arr.Count => arr[i],
                _ => null,
            };
            if (node is null) return null;
        }
        return node;
    }

    /// <summary>Keeps / renames only the listed fields. Arrays are projected element by element.</summary>
    public static JsonNode? Project(JsonNode? node, IReadOnlyList<string>? fields)
    {
        if (node is null) return null;
        if (fields is null || fields.Count == 0) return node.DeepClone();

        return node switch
        {
            JsonArray arr => new JsonArray(arr.Select(item => Project(item, fields)).ToArray()),
            JsonObject obj => ProjectObject(obj, fields),
            _ => node.DeepClone(),
        };
    }

    private static JsonObject ProjectObject(JsonObject obj, IReadOnlyList<string> fields)
    {
        var result = new JsonObject();
        foreach (var field in fields)
        {
            // "target=source.path" or just "name"
            var parts = field.Split('=', 2, StringSplitOptions.TrimEntries);
            var target = parts[0];
            var source = parts.Length == 2 ? parts[1] : parts[0];

            var value = Select(obj, source);
            if (value is not null) result[target] = value.DeepClone();
        }
        return result;
    }

    /// <summary>
    /// Combines two values that target the same response key:
    /// array + anything = concatenated array, object + object = merged object, otherwise the first wins.
    /// </summary>
    public static JsonNode? Combine(JsonNode? first, JsonNode? second)
    {
        if (first is null) return second?.DeepClone();
        if (second is null) return first.DeepClone();

        if (first is JsonArray || second is JsonArray)
        {
            var items = AsItems(first).Concat(AsItems(second)).Select(i => i?.DeepClone()).ToArray();
            return new JsonArray(items);
        }

        if (first is JsonObject a && second is JsonObject b) return MergeObjects(a, b);

        return first.DeepClone();
    }

    /// <summary>Merges b into a copy of a. Values already present in a win; nested objects merge recursively.</summary>
    public static JsonObject MergeObjects(JsonObject a, JsonObject b)
    {
        var result = (JsonObject)a.DeepClone();
        foreach (var (key, value) in b)
        {
            if (value is null) continue;

            var existing = result[key];
            if (existing is null)
                result[key] = value.DeepClone();
            else if (existing is JsonObject existingObj && value is JsonObject valueObj)
                result[key] = MergeObjects(existingObj, valueObj);
        }
        return result;
    }

    /// <summary>
    /// Removes duplicate objects that share the same identity field, merging them into the first occurrence.
    /// Keys compare by value, so 42 and "42" from different services are treated as the same entity.
    /// Items without the key (or non-objects) are kept untouched.
    /// </summary>
    public static JsonArray Dedupe(JsonArray items, string keyPath)
    {
        var result = new List<JsonNode?>(items.Count);
        var positions = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var item in items)
        {
            var key = item is JsonObject obj ? KeyOf(Select(obj, keyPath)) : null;
            if (key is null)
            {
                result.Add(item?.DeepClone());
                continue;
            }

            if (positions.TryGetValue(key, out var index))
            {
                result[index] = MergeObjects((JsonObject)result[index]!, (JsonObject)item!);
            }
            else
            {
                positions[key] = result.Count;
                result.Add(item!.DeepClone());
            }
        }

        return new JsonArray(result.ToArray());
    }

    private static string? KeyOf(JsonNode? node) => node is JsonValue value ? value.ToString() : null;

    private static IEnumerable<JsonNode?> AsItems(JsonNode node) => node is JsonArray arr ? arr : [node];
}
