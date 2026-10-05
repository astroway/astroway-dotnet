# Astroway.Sdk

Official .NET client for the [AstroWay API](https://api.astroway.info): natal charts, synastry, transits, Vedic dashas, Human Design, Tarot, numerology, SVG chart wheels and 700+ more endpoints.

[![NuGet](https://img.shields.io/nuget/v/Astroway.Sdk.svg)](https://www.nuget.org/packages/Astroway.Sdk)
[![license: MIT](https://img.shields.io/badge/license-MIT-green.svg)](LICENSE)

Async everywhere with `CancellationToken`, retries on 408/409/429/5xx with jittered backoff and `Retry-After`, one Idempotency-Key per POST kept across retries, one exception type per kind of failure. Targets .NET Standard 2.0 and .NET 8, so it runs on .NET Framework 4.7.2+, .NET 6/8/9 and Unity. The only dependency is `System.Text.Json`.

## Install

```bash
dotnet add package Astroway.Sdk
```

Get a key at <https://api.astroway.info/dashboard/sign-up>: 10 000 credits a month free, no card. Each endpoint costs 1 to 500 credits depending on what it computes ([pricing](https://api.astroway.info/pricing/)).

## Quick start

```csharp
using Astroway;

using var aw = new AstrowayClient(Environment.GetEnvironmentVariable("ASTROWAY_API_KEY")!);

var res = await aw.Chart.ComputeAsync(new
{
    date = "1990-07-14", time = "14:30:00", timezoneOffset = 3,
    latitude = 50.4501, longitude = 30.5234, houseSystem = "P",
});

Console.WriteLine(res.Data.GetProperty("houses").GetProperty("ascendant").GetDouble()); // 212.0952574979425
```

`/chart` returns positions, not labels: the ascendant and every planet's `longitude` are ecliptic longitudes in degrees, so the sign is `longitude / 30` into Aries..Pisces. A complete program that prints the sign and saves the wheel is in [`examples/Quickstart`](examples/Quickstart/Program.cs).

## How calls look

Every endpoint is a method on a namespace property, named after the API path:

| Endpoint | Method |
|---|---|
| `POST /chart` | `aw.Chart.ComputeAsync(body)` |
| `POST /synastry` | `aw.Synastry.ComputeAsync(body)` |
| `POST /vedic/dashas/vimshottari/maha` | `aw.Vedic.DashasVimshottariMahaAsync(body)` |
| `POST /render/wheel-western` | `aw.Render.WheelWesternAsync(body)` |
| `GET /health` | `aw.Health.ComputeAsync()` |

The body is any object `System.Text.Json` can serialise: an anonymous object, a dictionary or your own record. Property names go out camelCase and nulls are dropped, so `record Birth(string Date, double TimezoneOffset)` sends `{"date":…,"timezoneOffset":…}`.

The `{ ok, data, error }` envelope is unwrapped: `res.Data` is a `JsonElement`, and `res.Deserialize<T>()` fills your own type:

```csharp
record Planet(string Name, double Longitude);
record Houses(double Ascendant);
record Chart(Planet[] Planets, Houses Houses);

var chart = res.Deserialize<Chart>()!;
```

`res.RequestId` and `res.CreditsRemaining` come from the response headers. Anything without a generated method is one call away:

```csharp
await aw.PostAsync("/webhooks/wh_123/test");
```

### Query parameters

```csharp
await aw.Chart.ComputeAsync(body, new RequestOptions
{
    Query = new Dictionary<string, string> { ["fields"] = "planets", ["precision"] = "2", ["lang"] = "uk" },
});
```

### Draw a chart you already have, for 1 credit

`/render/*` computes the chart from birth data for 10 credits. When you already hold the chart from `Chart.ComputeAsync`, send it as is and only the drawing is charged:

```csharp
var img = await aw.DrawChartAsync(res.Data, renderOptions: new { theme = "css" });         // or kind: "aspect-grid"
var bi  = await aw.DrawBiWheelAsync(natal.Data, partner.Data, new { crossAspects = true });
string svg = img.Data.GetProperty("svg").GetString()!;
```

The SVG is byte for byte the one you would get from birth data. `theme = "css"` paints every colour as a CSS variable with stable `aw-*` classes, see [SVG charts](https://api.astroway.info/en/products/svg-charts/).

## Errors

```csharp
try
{
    await aw.Chart.ComputeAsync(body);
}
catch (QuotaExceededException) { /* out of credits; retrying will not help */ }
catch (RateLimitException e)   { /* still 429 after the retries; e.RetryAfter */ }
catch (BadRequestException e)  { Console.WriteLine($"{e.Code}: {e.Message} request_id={e.RequestId}"); }
catch (AstrowayException e)    { /* any other non-2xx: e.StatusCode, e.Body */ }
```

Kinds: `BadRequestException`, `AuthenticationException`, `PermissionDeniedException`, `NotFoundException`, `UnprocessableEntityException`, `RateLimitException`, `QuotaExceededException`, `CalculationException`, `InternalServerException`, all deriving from `AstrowayException`. A network failure after all retries is an `AstrowayConnectionException`. Quote the request id when you write to support.

## Options

```csharp
using var aw = new AstrowayClient(key, new AstrowayClientOptions
{
    MaxRetries = 4,                      // default 2, 0 disables
    Timeout = TimeSpan.FromSeconds(10),  // default 30 s
    BearerAuth = true,                   // Authorization: Bearer instead of X-Api-Key
    HttpClient = httpClientFactory.CreateClient("astroway"), // your own; the SDK will not dispose it
});
```

Per call: `RequestOptions { Query, Headers, IdempotencyKey }` and a `CancellationToken`.

## Links

- API reference: <https://api.astroway.info/docs/api/>
- Other SDKs: <https://api.astroway.info/sdk/>
- Changelog: <https://api.astroway.info/en/changelog/>

MIT licence.
