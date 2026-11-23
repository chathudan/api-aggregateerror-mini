using Microsoft.Extensions.Options;

namespace Aggregator.Api.Aggregation;

public static class AggregationEndpoints
{
    /// <summary>Registers options, validation and the services the composer needs.</summary>
    public static IServiceCollection AddAggregator(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AggregatorOptions>()
            .Bind(configuration.GetSection(AggregatorOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<AggregatorOptions>, AggregatorOptionsValidator>();

        services.AddHttpClient();
        services.AddSingleton<SourceClient>();
        services.AddSingleton<Composer>();
        return services;
    }

    /// <summary>Maps one GET endpoint per configured aggregation.</summary>
    public static IEndpointRouteBuilder MapAggregations(this IEndpointRouteBuilder app)
    {
        var options = app.ServiceProvider.GetRequiredService<IOptions<AggregatorOptions>>().Value;

        foreach (var aggregation in options.Aggregations)
        {
            app.MapGet(aggregation.Route, (HttpContext context, Composer composer, CancellationToken ct) =>
                    composer.ComposeAsync(aggregation, context, ct))
                .WithName(aggregation.Name)
                .WithTags("Aggregations");
        }

        return app;
    }
}
