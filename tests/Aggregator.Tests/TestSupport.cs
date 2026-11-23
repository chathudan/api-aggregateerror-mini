using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aggregator.Tests;

/// <summary>Stands in for every downstream service. Routes by "METHOD absolute-url".</summary>
public sealed class FakeDownstream : HttpMessageHandler
{
    private readonly ConcurrentDictionary<string, Func<HttpRequestMessage, Task<HttpResponseMessage>>> _routes = new();
    public ConcurrentQueue<HttpRequestMessage> Requests { get; } = new();

    public FakeDownstream Json(string url, string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        _routes[url] = _ => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });
        return this;
    }

    public FakeDownstream Status(string url, HttpStatusCode status)
    {
        _routes[url] = _ => Task.FromResult(new HttpResponseMessage(status));
        return this;
    }

    public FakeDownstream Hang(string url)
    {
        _routes[url] = async request =>
        {
            await Task.Delay(Timeout.Infinite, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
            return new HttpResponseMessage(HttpStatusCode.OK);
        };
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Enqueue(request);
        if (!_routes.TryGetValue(request.RequestUri!.AbsoluteUri, out var handler))
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        return await handler(request).WaitAsync(cancellationToken);
    }
}

/// <summary>Boots the real aggregator with test-only configuration and the fake downstream handler.</summary>
public sealed class AggregatorApp(Dictionary<string, string?> settings, FakeDownstream downstream) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration(config =>
        {
            config.Sources.Clear(); // ignore appsettings.json / aggregations.json
            config.AddInMemoryCollection(settings);
        });
        builder.ConfigureTestServices(services =>
            services.ConfigureHttpClientDefaults(http => http.ConfigurePrimaryHttpMessageHandler(() => downstream)));
    }
}
