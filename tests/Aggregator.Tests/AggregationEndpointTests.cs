using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Aggregator.Tests;

public class AggregationEndpointTests
{
    private const string A = "http://service-a.test/";
    private const string B = "http://service-b.test/";

    private static Dictionary<string, string?> BaseSettings() => new()
    {
        ["Aggregator:Services:ServiceA:BaseUrl"] = A,
        ["Aggregator:Services:ServiceB:BaseUrl"] = B,
        ["Aggregator:Services:ServiceB:TimeoutSeconds"] = "1",
        ["Aggregator:ForwardHeaders:0"] = "Authorization",
        ["Aggregator:ForwardHeaders:1"] = "X-Tenant-Id",

        // team-overview: same users from two services, different field names, deduped by id
        ["Aggregator:Aggregations:0:Name"] = "team-overview",
        ["Aggregator:Aggregations:0:Route"] = "/bff/teams/{teamId}",
        ["Aggregator:Aggregations:0:Sources:0:Service"] = "ServiceB",
        ["Aggregator:Aggregations:0:Sources:0:Path"] = "teams/{teamId}",
        ["Aggregator:Aggregations:0:Sources:0:Into"] = "team",
        ["Aggregator:Aggregations:0:Sources:0:Required"] = "true",
        ["Aggregator:Aggregations:0:Sources:0:Fields:0"] = "id=teamId",
        ["Aggregator:Aggregations:0:Sources:0:Fields:1"] = "name",
        ["Aggregator:Aggregations:0:Sources:1:Service"] = "ServiceA",
        ["Aggregator:Aggregations:0:Sources:1:Path"] = "users?teamId={teamId}",
        ["Aggregator:Aggregations:0:Sources:1:Select"] = "data",
        ["Aggregator:Aggregations:0:Sources:1:Into"] = "members",
        ["Aggregator:Aggregations:0:Sources:2:Service"] = "ServiceB",
        ["Aggregator:Aggregations:0:Sources:2:Path"] = "teams/{teamId}",
        ["Aggregator:Aggregations:0:Sources:2:Select"] = "members",
        ["Aggregator:Aggregations:0:Sources:2:Into"] = "members",
        ["Aggregator:Aggregations:0:Sources:2:Fields:0"] = "id=userId",
        ["Aggregator:Aggregations:0:Sources:2:Fields:1"] = "role",
        ["Aggregator:Aggregations:0:DedupeBy:members"] = "id",

        // search: placeholder from the query string
        ["Aggregator:Aggregations:1:Name"] = "search",
        ["Aggregator:Aggregations:1:Route"] = "/bff/search",
        ["Aggregator:Aggregations:1:Sources:0:Service"] = "ServiceA",
        ["Aggregator:Aggregations:1:Sources:0:Path"] = "users?q={q}",
        ["Aggregator:Aggregations:1:Sources:0:Into"] = "users",
    };

    private static async Task<(HttpStatusCode Status, JsonNode? Body)> Get(AggregatorApp app, string url, Action<HttpRequestMessage>? configure = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        configure?.Invoke(request);
        using var response = await app.CreateClient().SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? null : JsonNode.Parse(text));
    }

    private static FakeDownstream HappyTeam() => new FakeDownstream()
        .Json($"{B}teams/10", """{"teamId":10,"name":"Platform","members":[{"userId":1,"role":"Lead"},{"userId":99,"role":"Guest"}]}""")
        .Json($"{A}users?teamId=10", """{"data":[{"id":1,"name":"Ada"},{"id":2,"name":"Alan"}]}""");

    [Fact]
    public async Task Composes_renames_and_dedupes_users_from_different_services()
    {
        var downstream = HappyTeam();
        await using var app = new AggregatorApp(BaseSettings(), downstream);

        var (status, body) = await Get(app, "/bff/teams/10");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(
            """{"team":{"id":10,"name":"Platform"},"members":[{"id":1,"name":"Ada","role":"Lead"},{"id":2,"name":"Alan"},{"id":99,"role":"Guest"}]}""",
            body!.ToJsonString());
    }

    [Fact]
    public async Task Identical_downstream_calls_are_made_once_per_request()
    {
        var downstream = HappyTeam();
        await using var app = new AggregatorApp(BaseSettings(), downstream);

        await Get(app, "/bff/teams/10");

        Assert.Single(downstream.Requests, r => r.RequestUri!.AbsoluteUri == $"{B}teams/10");
    }

    [Fact]
    public async Task Optional_source_failure_returns_partial_response_with_errors()
    {
        var downstream = HappyTeam().Status($"{A}users?teamId=10", HttpStatusCode.ServiceUnavailable);
        await using var app = new AggregatorApp(BaseSettings(), downstream);

        var (status, body) = await Get(app, "/bff/teams/10");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(2, body!["members"]!.AsArray().Count);
        var error = body["_errors"]!.AsArray().Single()!;
        Assert.Equal("ServiceA", error["source"]!.GetValue<string>());
        Assert.Equal(503, error["status"]!.GetValue<int>());
    }

    [Fact]
    public async Task Required_source_4xx_is_passed_through()
    {
        await using var app = new AggregatorApp(BaseSettings(), new FakeDownstream().Status($"{B}teams/10", HttpStatusCode.NotFound));

        var (status, body) = await Get(app, "/bff/teams/10");

        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Contains("ServiceB", body!["title"]!.GetValue<string>());
    }

    [Fact]
    public async Task Required_source_5xx_becomes_bad_gateway()
    {
        await using var app = new AggregatorApp(BaseSettings(), HappyTeam().Status($"{B}teams/10", HttpStatusCode.InternalServerError));

        var (status, _) = await Get(app, "/bff/teams/10");

        Assert.Equal(HttpStatusCode.BadGateway, status);
    }

    [Fact]
    public async Task Required_source_timeout_becomes_gateway_timeout()
    {
        await using var app = new AggregatorApp(BaseSettings(), HappyTeam().Hang($"{B}teams/10"));

        var (status, _) = await Get(app, "/bff/teams/10");

        Assert.Equal(HttpStatusCode.GatewayTimeout, status);
    }

    [Fact]
    public async Task Required_source_returning_invalid_json_becomes_bad_gateway()
    {
        await using var app = new AggregatorApp(BaseSettings(), HappyTeam().Json($"{B}teams/10", "not json"));

        var (status, _) = await Get(app, "/bff/teams/10");

        Assert.Equal(HttpStatusCode.BadGateway, status);
    }

    [Fact]
    public async Task Query_string_values_fill_placeholders_and_are_url_encoded()
    {
        var downstream = new FakeDownstream().Json($"{A}users?q=a%20%26%20b", """[{"id":1}]""");
        await using var app = new AggregatorApp(BaseSettings(), downstream);

        var (status, body) = await Get(app, "/bff/search?q=a%20%26%20b");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("""{"users":[{"id":1}]}""", body!.ToJsonString());
    }

    [Fact]
    public async Task Missing_placeholder_value_returns_400_without_calling_downstream()
    {
        var downstream = new FakeDownstream();
        await using var app = new AggregatorApp(BaseSettings(), downstream);

        var (status, _) = await Get(app, "/bff/search");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Empty(downstream.Requests);
    }

    [Fact]
    public async Task Configured_headers_are_forwarded_others_are_not()
    {
        var downstream = HappyTeam();
        await using var app = new AggregatorApp(BaseSettings(), downstream);

        await Get(app, "/bff/teams/10", r =>
        {
            r.Headers.TryAddWithoutValidation("Authorization", "Bearer token-123");
            r.Headers.TryAddWithoutValidation("x-tenant-id", "acme"); // matching is case-insensitive
            r.Headers.TryAddWithoutValidation("X-Not-Forwarded", "nope");
        });

        Assert.All(downstream.Requests, r =>
        {
            Assert.Equal("Bearer token-123", r.Headers.Authorization!.ToString());
            Assert.Equal("acme", r.Headers.GetValues("X-Tenant-Id").Single());
            Assert.False(r.Headers.Contains("X-Not-Forwarded"));
        });
    }

    [Theory]
    [InlineData("Aggregator:Aggregations:0:Sources:1:Service", "ServiceZ", "unknown service 'ServiceZ'")]
    [InlineData("Aggregator:Aggregations:0:DedupeBy:people", "id", "DedupeBy key 'people'")]
    [InlineData("Aggregator:Services:ServiceA:BaseUrl", "not-a-url", "absolute http(s) URL")]
    [InlineData("Aggregator:Aggregations:1:Route", "/bff/teams/{teamId}", "duplicate Route")]
    [InlineData("Aggregator:Aggregations:0:Sources:1:Into", "_errors", "reserved")]
    public void Invalid_configuration_fails_at_startup(string key, string value, string expectedMessage)
    {
        var settings = BaseSettings();
        settings[key] = value;
        using var app = new AggregatorApp(settings, new FakeDownstream());

        var ex = Assert.ThrowsAny<OptionsValidationException>(() => app.CreateClient());
        Assert.Contains(expectedMessage, ex.Message);
    }

    [Fact]
    public void ForwardHeaders_come_only_from_configuration()
    {
        using var app = new AggregatorApp(BaseSettings(), new FakeDownstream());

        var options = app.Services.GetRequiredService<IOptions<Aggregator.Api.Aggregation.AggregatorOptions>>().Value;

        Assert.Equal(["Authorization", "X-Tenant-Id"], options.ForwardHeaders);
    }
}
