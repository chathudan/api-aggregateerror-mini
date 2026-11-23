namespace Aggregator.Api.Aggregation;

/// <summary>
/// Root configuration (section "Aggregator").
/// Services live in appsettings.{env}.json (they change per environment);
/// Aggregations live in aggregations.json (they describe the BFF contract).
/// </summary>
public sealed class AggregatorOptions
{
    public const string SectionName = "Aggregator";

    /// <summary>Downstream services keyed by a logical name, e.g. "ServiceA".</summary>
    public Dictionary<string, ServiceOptions> Services { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Each entry becomes one GET endpoint on the BFF.</summary>
    public List<AggregationDefinition> Aggregations { get; init; } = [];

    /// <summary>
    /// Incoming request headers copied onto every downstream call (case-insensitive).
    /// Configure in appsettings.json. Deliberately no C# default: the configuration binder
    /// appends to an initialised array instead of replacing it.
    /// </summary>
    public string[] ForwardHeaders { get; init; } = [];
}

public sealed class ServiceOptions
{
    public string BaseUrl { get; init; } = "";
    public int TimeoutSeconds { get; init; } = 10;
}

public sealed class AggregationDefinition
{
    /// <summary>Unique endpoint name (used for logging and endpoint metadata).</summary>
    public string Name { get; init; } = "";

    /// <summary>BFF route, e.g. "/bff/teams/{teamId}". Route values feed source path placeholders.</summary>
    public string Route { get; init; } = "";

    /// <summary>Downstream calls. Fetched in parallel, composed in the order declared.</summary>
    public List<SourceDefinition> Sources { get; init; } = [];

    /// <summary>
    /// Collections to de-duplicate after composition: response key -> identity field.
    /// Duplicates are merged into one object (first source wins on conflicting values,
    /// later sources fill in fields that are missing).
    /// </summary>
    public Dictionary<string, string> DedupeBy { get; init; } = new(StringComparer.Ordinal);
}

public sealed class SourceDefinition
{
    /// <summary>Key into <see cref="AggregatorOptions.Services"/>.</summary>
    public string Service { get; init; } = "";

    /// <summary>Relative path, may contain {placeholders} resolved from route values, then query string.</summary>
    public string Path { get; init; } = "";

    /// <summary>Optional dot path into the downstream response, e.g. "data.items".</summary>
    public string? Select { get; init; }

    /// <summary>
    /// Optional projection. "name" keeps a field; "id=userId" renames userId to id;
    /// "city=address.city" flattens a nested value. Applied to every element of an array.
    /// </summary>
    public string[]? Fields { get; init; }

    /// <summary>Top-level response key. Several sources may target the same key (they get merged).</summary>
    public string Into { get; init; } = "";

    /// <summary>When true a failure of this source fails the whole request; otherwise it is reported in "_errors".</summary>
    public bool Required { get; init; }
}
