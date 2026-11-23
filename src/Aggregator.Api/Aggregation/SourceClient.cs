using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace Aggregator.Api.Aggregation;

public sealed record SourceError(int? StatusCode, string Message);

public sealed record SourceResult(JsonNode? Body, SourceError? Error)
{
    public static SourceResult Ok(JsonNode? body) => new(body, null);
    public static SourceResult Fail(int? status, string message) => new(null, new SourceError(status, message));
}

/// <summary>
/// Calls one downstream service. Never throws for downstream problems (HTTP errors, timeouts,
/// invalid JSON) – it returns a <see cref="SourceResult"/> so the composer can decide what to do.
/// Uses a named HttpClient per service, so you can add handlers per service with
/// builder.Services.AddHttpClient("ServiceA").AddHttpMessageHandler(...) if ever needed.
/// </summary>
public sealed class SourceClient(
    IHttpClientFactory httpClientFactory,
    IOptions<AggregatorOptions> options,
    ILogger<SourceClient> logger)
{
    public async Task<SourceResult> GetAsync(
        string serviceName,
        string relativePath,
        IReadOnlyList<KeyValuePair<string, string>> headers,
        CancellationToken cancellationToken)
    {
        var service = options.Value.Services[serviceName];
        var baseUri = new Uri(service.BaseUrl.EndsWith('/') ? service.BaseUrl : service.BaseUrl + "/");
        var uri = new Uri(baseUri, relativePath.TrimStart('/'));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(service.TimeoutSeconds));

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        foreach (var (name, value) in headers) request.Headers.TryAddWithoutValidation(name, value);

        try
        {
            var client = httpClientFactory.CreateClient(serviceName);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("{Service} GET {Uri} returned {Status}", serviceName, uri, (int)response.StatusCode);
                return SourceResult.Fail((int)response.StatusCode, $"{serviceName} returned {(int)response.StatusCode}.");
            }

            if (response.StatusCode == HttpStatusCode.NoContent || response.Content.Headers.ContentLength == 0)
                return SourceResult.Ok(null);

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            return SourceResult.Ok(await JsonNode.ParseAsync(stream, cancellationToken: timeout.Token));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("{Service} GET {Uri} timed out after {Timeout}s", serviceName, uri, service.TimeoutSeconds);
            return SourceResult.Fail(StatusCodes.Status504GatewayTimeout, $"{serviceName} timed out.");
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "{Service} GET {Uri} failed", serviceName, uri);
            return SourceResult.Fail(null, $"{serviceName} is unreachable.");
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "{Service} GET {Uri} returned invalid JSON", serviceName, uri);
            return SourceResult.Fail(null, $"{serviceName} returned invalid JSON.");
        }
    }
}
