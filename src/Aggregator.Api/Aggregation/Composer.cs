using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace Aggregator.Api.Aggregation;

/// <summary>
/// Executes one <see cref="AggregationDefinition"/>:
///   1. resolve every source path from the incoming request
///   2. call all sources in parallel
///   3. shape each response (Select -> Fields) and place it under its "Into" key
///   4. de-duplicate configured collections
/// </summary>
public sealed class Composer(SourceClient sourceClient, IOptions<AggregatorOptions> options)
{
    /// <summary>Response key listing optional sources that failed (only present when something failed).</summary>
    public const string ErrorsKey = "_errors";

    public async Task<IResult> ComposeAsync(AggregationDefinition aggregation, HttpContext context, CancellationToken cancellationToken)
    {
        // 1. Resolve paths up-front so a bad request never hits downstream services.
        var paths = new string[aggregation.Sources.Count];
        for (var i = 0; i < paths.Length; i++)
        {
            if (!PathTemplate.TryResolve(aggregation.Sources[i].Path, context.Request, out paths[i], out var missing))
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest,
                    title: "Missing parameter", detail: $"Parameter '{missing}' is required.");
        }

        // 2. Fetch everything concurrently. Sources hitting the same service + path share one call
        //    (e.g. "team" and "members" both read from GET teams/{teamId}).
        var headers = ForwardedHeaders(context.Request);
        var calls = new Dictionary<(string Service, string Path), Task<SourceResult>>();
        var pending = aggregation.Sources.Select((source, i) =>
        {
            var key = (source.Service.ToUpperInvariant(), paths[i]);
            if (!calls.TryGetValue(key, out var call))
                calls[key] = call = sourceClient.GetAsync(source.Service, paths[i], headers, cancellationToken);
            return call;
        }).ToArray();
        var results = await Task.WhenAll(pending);

        // 3. Compose in declaration order (deterministic regardless of which call finished first).
        var response = new JsonObject();
        var errors = new JsonArray();

        for (var i = 0; i < results.Length; i++)
        {
            var source = aggregation.Sources[i];
            var result = results[i];

            if (result.Error is { } error)
            {
                if (source.Required) return RequiredSourceFailed(source, error);

                errors.Add(new JsonObject
                {
                    ["source"] = source.Service,
                    ["into"] = source.Into,
                    ["status"] = error.StatusCode,
                    ["message"] = error.Message,
                });
                continue;
            }

            var shaped = JsonShaping.Project(JsonShaping.Select(result.Body, source.Select), source.Fields);
            Set(response, source.Into, JsonShaping.Combine(Detach(response, source.Into), shaped));
        }

        // 4. Remove duplicates that came from different endpoints (e.g. the same user from Service A and B).
        foreach (var (key, identityField) in aggregation.DedupeBy)
        {
            if (Detach(response, key) is JsonArray items)
                Set(response, key, JsonShaping.Dedupe(items, identityField));
        }

        if (errors.Count > 0) response[ErrorsKey] = errors;

        return Results.Json(response);
    }

    private static IResult RequiredSourceFailed(SourceDefinition source, SourceError error)
    {
        // A downstream 4xx (e.g. 404 user not found, 401) is meaningful to the caller: pass it on.
        // Anything else is a gateway problem.
        var status = error.StatusCode switch
        {
            >= 400 and < 500 => error.StatusCode.Value,
            StatusCodes.Status504GatewayTimeout => StatusCodes.Status504GatewayTimeout,
            _ => StatusCodes.Status502BadGateway,
        };
        return Results.Problem(statusCode: status, title: $"Required source '{source.Service}' failed", detail: error.Message);
    }

    private List<KeyValuePair<string, string>> ForwardedHeaders(HttpRequest request) =>
        options.Value.ForwardHeaders
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(name => request.Headers.ContainsKey(name))
            .Select(name => KeyValuePair.Create(name, request.Headers[name].ToString()))
            .ToList();

    /// <summary>Removes and returns a property so the node can be re-used without a "node already has a parent" error.</summary>
    private static JsonNode? Detach(JsonObject obj, string key)
    {
        if (!obj.TryGetPropertyValue(key, out var value)) return null;
        obj.Remove(key);
        return value;
    }

    private static void Set(JsonObject obj, string key, JsonNode? value)
    {
        if (value is not null) obj[key] = value;
    }
}
