using Aggregator.Api.Aggregation;

var builder = WebApplication.CreateBuilder(args);

// BFF contracts live in their own file so developers only touch one place to add/change an aggregation.
builder.Configuration.AddJsonFile("aggregations.json", optional: false, reloadOnChange: false);

builder.Services.AddProblemDetails();
builder.Services.AddAggregator(builder.Configuration);

var app = builder.Build();

app.UseExceptionHandler();
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
app.MapAggregations();

app.Run();

public partial class Program; // exposed for integration tests
