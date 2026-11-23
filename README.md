# API Aggregator Mini (BFF)

A small, config-driven **API aggregator & composer** for Backend-for-Frontend use cases, built on
.NET 10 Minimal APIs. **No third-party runtime packages**: only the ASP.NET Core shared framework.
(Tests use xUnit and Microsoft.AspNetCore.Mvc.Testing, both free and open source.)

A developer describes **what** a BFF endpoint returns in `aggregations.json`. The aggregator then
calls the services, shapes each response, merges everything into one model and removes duplicate
objects (for example the same User returned by both Service A and Service B).

```
           GET /bff/teams/10
                  │
          ┌───────▼────────┐   parallel    ┌────────────┐
          │   Aggregator   ├──────────────►│ Service A  │ users?teamId=10
          │  (this app)    ├──────────────►│ Service B  │ teams/10
          └───────┬────────┘               └────────────┘
                  │  select → project → merge → dedupe
                  ▼
   { "team": {...}, "members": [ ...unique users... ] }
```

## Run it

```bash
dotnet run --project src/MockServices     # fake Service A/B/C on http://localhost:5100
dotnet run --project src/Aggregator.Api   # BFF on http://localhost:5180

curl localhost:5180/bff/teams/10            # users from A + B, deduplicated
curl localhost:5180/bff/users/1/dashboard   # user + profile merged, orders, notifications
curl localhost:5180/bff/users/2/dashboard   # Service C is "down": partial response with _errors
curl localhost:5180/bff/users/42/dashboard  # required source 404 -> 404
dotnet test
```

## Project layout

```
src/Aggregator.Api/
  Program.cs                      wiring only (about 15 lines)
  appsettings.json                downstream services (BaseUrl, timeout) per environment
  aggregations.json               the BFF contracts: edit this file to add or change endpoints
  Aggregation/
    AggregatorOptions.cs          config model (documented properties)
    AggregatorOptionsValidator.cs fails fast at start-up on config mistakes
    AggregationEndpoints.cs       AddAggregator() / MapAggregations(): one GET per aggregation
    Composer.cs                   resolve paths → fetch in parallel → compose → dedupe
    SourceClient.cs               one downstream GET: timeout, header forwarding, errors as values
    JsonShaping.cs                pure JSON helpers: Select / Project / Combine / Merge / Dedupe
    PathTemplate.cs               {placeholder} resolution
src/MockServices/                 fake Service A/B/C for local development
tests/Aggregator.Tests/           unit tests (JsonShaping) and HTTP-level tests (fake downstream)
```

## Defining an aggregation

```jsonc
{
  "Name": "team-overview",
  "Route": "/bff/teams/{teamId}",
  "Sources": [
    { "Service": "ServiceB", "Path": "teams/{teamId}", "Into": "team", "Required": true,
      "Fields": [ "id=teamId", "name" ] },
    { "Service": "ServiceA", "Path": "users?teamId={teamId}", "Select": "data", "Into": "members",
      "Fields": [ "id", "name", "email" ] },
    { "Service": "ServiceB", "Path": "teams/{teamId}", "Select": "members", "Into": "members",
      "Fields": [ "id=userId", "name=displayName", "role" ] }
  ],
  "DedupeBy": { "members": "id" }
}
```

| Property | Meaning |
|---|---|
| `Route` | BFF route (ASP.NET route syntax, constraints allowed, e.g. `{id:int}`). |
| `Service` | Key in `Aggregator:Services` (appsettings). |
| `Path` | Relative downstream path. `{name}` comes from route values, then the query string, and is URL-encoded. A missing value returns **400** before any call is made. |
| `Select` | Optional dot path into the response (`data`, `result.items`, `items.0`). |
| `Fields` | Optional projection: `name` keeps a field, `id=userId` renames it, `city=address.city` flattens it. Applies to each element of an array. Leave it out to return everything. |
| `Into` | Top-level key in the BFF response. **Several sources may share a key**: arrays are concatenated, objects are merged. |
| `Required` | `true`: a failure fails the request (downstream 4xx is passed through, timeout gives 504, anything else gives 502). `false` (default): the failure is listed in `_errors` and the rest is returned. |
| `DedupeBy` | `responseKey → identityField`. Objects with the same identity are **merged into one**. The first source in declaration order wins on conflicting values; later sources fill in missing fields. `1` and `"1"` count as the same id. Items without the field are kept as they are. |

### Composition rules (in one place)
1. All sources run **in parallel**. Identical `Service` + resolved `Path` calls are made **once** per request.
2. Results are composed in **declaration order**, so output is deterministic.
3. For each source: `Select` → `Fields` → merge into `Into`.
4. `DedupeBy` runs last, over the composed collections.
5. `_errors` is added only when an optional source failed.

### Global settings (`appsettings.json`)
```json
"Aggregator": {
  "ForwardHeaders": [ "Authorization", "X-Correlation-Id" ],
  "Services": { "ServiceA": { "BaseUrl": "https://a.internal/", "TimeoutSeconds": 5 } }
}
```
`ForwardHeaders` is the complete list (there are no hidden defaults in code). Header names are
case-insensitive. To pass a bearer token, keep `Authorization`: the client sends
`Authorization: Bearer <token>` and it is forwarded as it is.

Override per environment with `appsettings.Production.json` or environment variables
(`Aggregator__Services__ServiceA__BaseUrl=...`).

## Extending
- **Per-service HTTP behaviour** (auth token handler, resilience, mTLS): each service uses a named
  `HttpClient` that matches its key, e.g. `builder.Services.AddHttpClient("ServiceA").AddHttpMessageHandler<MyAuthHandler>()`.
- **An endpoint that config can't express** (calls that depend on another call's result, heavy business logic):
  write a normal Minimal API endpoint next to `MapAggregations()` and reuse `SourceClient` and `JsonShaping`.
  Keeping those rare cases in code keeps the config model small.

## Deliberately out of scope
Chained/dependent calls, non-GET methods, response caching, GraphQL-style client field selection.
Each one is a small, isolated addition if you ever need it.
