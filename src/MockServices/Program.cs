// Fake downstream services for local development. Three "services" in one process,
// each under its own path prefix. Note they deliberately disagree on field names
// (Service A: id/name, Service B: userId/displayName) – the aggregator normalises them.
var app = WebApplication.CreateBuilder(args).Build();

var users = new[]
{
    new { id = 1, name = "Ada Lovelace", email = "ada@example.com", teamId = 10 },
    new { id = 2, name = "Alan Turing", email = "alan@example.com", teamId = 10 },
    new { id = 3, name = "Grace Hopper", email = "grace@example.com", teamId = 20 },
};

// ---------- Service A: users & orders ----------
var a = app.MapGroup("/service-a");
a.MapGet("/users/{id:int}", (int id) =>
    users.FirstOrDefault(u => u.id == id) is { } u ? Results.Ok(u) : Results.NotFound());
a.MapGet("/users", (int? teamId) =>
    Results.Ok(new { data = users.Where(u => teamId is null || u.teamId == teamId) }));
a.MapGet("/users/{id:int}/orders", (int id) => Results.Ok(new
{
    items = new[]
    {
        new { orderId = $"ORD-{id}01", total = 120.50m, status = "Shipped", internalCode = "X1" },
        new { orderId = $"ORD-{id}02", total = 15.00m, status = "Pending", internalCode = "X2" },
    },
}));

// ---------- Service B: teams (members overlap with Service A users) ----------
var b = app.MapGroup("/service-b");
b.MapGet("/teams/{id:int}", (int id) => id switch
{
    10 => Results.Ok(new
    {
        teamId = 10,
        name = "Platform",
        members = new object[]
        {
            new { userId = 1, displayName = "Ada L.", role = "Lead" },     // duplicate of Service A user 1
            new { userId = 2, displayName = "Alan T.", role = "Engineer" }, // duplicate of Service A user 2
            new { userId = 99, displayName = "Contractor", role = "Guest" },// only known to Service B
        },
    }),
    _ => Results.NotFound(),
});

// ---------- Service C: profiles & notifications ----------
var c = app.MapGroup("/service-c");
c.MapGet("/profiles/{userId:int}", (int userId) =>
    Results.Ok(new { userId, avatarUrl = $"https://cdn.example.com/avatars/{userId}.png", timezone = "Australia/Sydney" }));
c.MapGet("/notifications", (int userId) =>
    userId == 2
        ? Results.StatusCode(StatusCodes.Status503ServiceUnavailable) // simulate an outage for user 2
        : Results.Ok(new[] { new { id = "n1", text = "Your order shipped" } }));

app.Run();
