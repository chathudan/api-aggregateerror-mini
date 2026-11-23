using System.Text.RegularExpressions;

namespace Aggregator.Api.Aggregation;

/// <summary>Resolves "{name}" placeholders from route values first, then the query string.</summary>
public static partial class PathTemplate
{
    [GeneratedRegex(@"\{(\w+)\}")]
    private static partial Regex Placeholder();

    public static bool TryResolve(string template, HttpRequest request, out string path, out string? missing)
    {
        string? firstMissing = null;

        path = Placeholder().Replace(template, match =>
        {
            var name = match.Groups[1].Value;
            var value = request.RouteValues.TryGetValue(name, out var routeValue) ? routeValue?.ToString() : null;
            if (string.IsNullOrEmpty(value)) value = request.Query[name].FirstOrDefault();

            if (string.IsNullOrEmpty(value))
            {
                firstMissing ??= name;
                return match.Value;
            }
            return Uri.EscapeDataString(value);
        });

        missing = firstMissing;
        return missing is null;
    }
}
