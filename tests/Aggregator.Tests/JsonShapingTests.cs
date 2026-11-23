using System.Text.Json.Nodes;
using Aggregator.Api.Aggregation;

namespace Aggregator.Tests;

public class JsonShapingTests
{
    private static JsonNode Json(string json) => JsonNode.Parse(json)!;

    [Fact]
    public void Select_walks_objects_and_array_indexes()
    {
        var node = Json("""{"data":{"items":[{"id":1},{"id":2}]}}""");

        Assert.Equal(2, JsonShaping.Select(node, "data.items.1.id")!.GetValue<int>());
        Assert.Null(JsonShaping.Select(node, "data.missing"));
        Assert.Same(node, JsonShaping.Select(node, null));
    }

    [Fact]
    public void Project_keeps_renames_and_flattens_fields_for_each_array_item()
    {
        var node = Json("""[{"userId":1,"displayName":"Ada","secret":"x","address":{"city":"Sydney"}}]""");

        var result = JsonShaping.Project(node, ["id=userId", "displayName", "city=address.city", "missing"]);

        Assert.Equal("""[{"id":1,"displayName":"Ada","city":"Sydney"}]""", result!.ToJsonString());
    }

    [Fact]
    public void Combine_concatenates_arrays_and_merges_objects()
    {
        Assert.Equal("[1,2,3]", JsonShaping.Combine(Json("[1]"), Json("[2,3]"))!.ToJsonString());
        Assert.Equal("""[{"a":1},2]""", JsonShaping.Combine(Json("""{"a":1}"""), Json("[2]"))!.ToJsonString());
        Assert.Equal("""{"a":1,"b":2}""", JsonShaping.Combine(Json("""{"a":1}"""), Json("""{"a":9,"b":2}"""))!.ToJsonString());
        Assert.Equal("\"first\"", JsonShaping.Combine(Json("\"first\""), Json("\"second\""))!.ToJsonString());
    }

    [Fact]
    public void MergeObjects_first_wins_fills_missing_and_merges_nested()
    {
        var merged = JsonShaping.MergeObjects(
            Json("""{"id":1,"name":"Ada","email":null,"meta":{"a":1}}""").AsObject(),
            Json("""{"id":1,"name":"Ada L.","email":"ada@x.com","role":"Lead","meta":{"b":2}}""").AsObject());

        Assert.Equal("""{"id":1,"name":"Ada","email":"ada@x.com","meta":{"a":1,"b":2},"role":"Lead"}""", merged.ToJsonString());
    }

    [Fact]
    public void Dedupe_merges_objects_with_same_key_across_number_and_string_ids()
    {
        var items = Json("""
            [
              {"id":1,"name":"Ada","email":"ada@x.com"},
              {"id":2,"name":"Alan"},
              {"id":"1","name":"Ada L.","role":"Lead"},
              {"name":"no id"},
              {"id":2,"role":"Engineer"}
            ]
            """).AsArray();

        var result = JsonShaping.Dedupe(items, "id");

        Assert.Equal(
            """[{"id":1,"name":"Ada","email":"ada@x.com","role":"Lead"},{"id":2,"name":"Alan","role":"Engineer"},{"name":"no id"}]""",
            result.ToJsonString());
    }

    [Fact]
    public void Helpers_never_reparent_input_nodes()
    {
        var source = Json("""{"items":[{"id":1}]}""");
        var selected = JsonShaping.Select(source, "items");

        var target = new JsonObject { ["copy"] = JsonShaping.Project(selected, null) };

        Assert.NotNull(source["items"]); // original untouched
        Assert.Equal("""{"copy":[{"id":1}]}""", target.ToJsonString());
    }
}
