using Microsoft.Extensions.Options;

namespace Aggregator.Api.Aggregation;

/// <summary>Fails application start-up on configuration mistakes instead of at request time.</summary>
public sealed class AggregatorOptionsValidator : IValidateOptions<AggregatorOptions>
{
    public ValidateOptionsResult Validate(string? name, AggregatorOptions options)
    {
        var errors = new List<string>();

        foreach (var (serviceName, service) in options.Services)
        {
            if (!Uri.TryCreate(service.BaseUrl, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
                errors.Add($"Service '{serviceName}': BaseUrl '{service.BaseUrl}' must be an absolute http(s) URL.");
            if (service.TimeoutSeconds <= 0)
                errors.Add($"Service '{serviceName}': TimeoutSeconds must be greater than 0.");
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var routes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var agg in options.Aggregations)
        {
            var label = string.IsNullOrWhiteSpace(agg.Name) ? agg.Route : agg.Name;

            if (string.IsNullOrWhiteSpace(agg.Name)) errors.Add($"Aggregation '{label}': Name is required.");
            else if (!names.Add(agg.Name)) errors.Add($"Aggregation '{label}': duplicate Name.");

            if (string.IsNullOrWhiteSpace(agg.Route) || !agg.Route.StartsWith('/'))
                errors.Add($"Aggregation '{label}': Route is required and must start with '/'.");
            else if (!routes.Add(agg.Route)) errors.Add($"Aggregation '{label}': duplicate Route '{agg.Route}'.");

            if (agg.Sources.Count == 0) errors.Add($"Aggregation '{label}': at least one source is required.");

            foreach (var source in agg.Sources)
            {
                if (!options.Services.ContainsKey(source.Service))
                    errors.Add($"Aggregation '{label}': source references unknown service '{source.Service}'.");
                if (string.IsNullOrWhiteSpace(source.Into))
                    errors.Add($"Aggregation '{label}': source '{source.Service}:{source.Path}' needs an 'Into' key.");
                else if (source.Into == Composer.ErrorsKey)
                    errors.Add($"Aggregation '{label}': '{Composer.ErrorsKey}' is reserved and cannot be used as 'Into'.");
            }

            foreach (var key in agg.DedupeBy.Keys)
            {
                if (!agg.Sources.Any(s => s.Into == key))
                    errors.Add($"Aggregation '{label}': DedupeBy key '{key}' does not match any source 'Into'.");
            }
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
