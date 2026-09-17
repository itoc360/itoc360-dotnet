# ITOC360 .NET SDK

Official .NET SDK for [ITOC360](https://itoc360.com) — send alerts from your
services to ITOC360, where they are deduplicated, prioritised, escalated and
routed to the people on call.

[![NuGet](https://img.shields.io/nuget/v/ITOC360.Sdk.svg)](https://www.nuget.org/packages/ITOC360.Sdk/)

No external dependencies. Everything it needs — `HttpClient` and
`System.Text.Json` — ships with the framework.

## Install

```bash
dotnet add package ITOC360.Sdk
```

Targets .NET 8 and later. Trimming and native AOT are supported.

## Getting a token

In ITOC360, go to **Sources**, click **Create Source**, and pick the provider
your alerts speak. The source page then shows a token.

The token identifies both your organization and the payload format the endpoint
expects, so keep it in your secret store and out of source control. Anyone who
can read it can raise alerts in your organization.

## Sending an alert

```csharp
using Itoc360;

using var client = new Itoc360Client(
    Environment.GetEnvironmentVariable("ITOC360_TOKEN")!);

var stored = await client.SendAlertAsync(new Alert
{
    Fingerprint = "db-01:disk",
    Summary     = "Disk almost full on db-01",
    Description = "92% of 500G used, growing 4G/hour",
    Severity    = Severity.Critical,
    Labels      = new Dictionary<string, string>
    {
        ["instance"] = "db-01",
        ["service"]  = "postgres",
    },
});

Console.WriteLine(stored.Id);
```

`SendAlertAsync` sends the Prometheus Alertmanager webhook payload, so the
source must be configured for a provider that speaks it: **prometheus**,
**mimir**, **cortex**, **loki**, **signoz** or **grafana**. For any other
provider, see [Other providers](#other-providers).

## Resolving it

Send the same fingerprint with `Status.Resolved`. ITOC360 closes the alert that
fingerprint opened, rather than raising a new one.

```csharp
await client.SendAlertAsync(new Alert
{
    Fingerprint = "db-01:disk",
    Summary     = "Disk almost full on db-01",
    Status      = Status.Resolved,
});
```

The fingerprint is what ties the two events together, so derive it from whatever
makes the condition unique — the host, the check and the object it watches — and
keep it stable across the alert's lifetime.

## Severity

| Value | Priority in ITOC360 |
| --- | --- |
| `Severity.Critical` | CRITICAL |
| `Severity.Error` | HIGH |
| `Severity.Warning` | MEDIUM (the default) |
| `Severity.Info` | LOW |

## Other providers

`SendRawAsync` applies no shape of its own, so it works with every provider
ITOC360 supports. Pass the body that provider documents.

```csharp
using System.Text.Json.Nodes;

await client.SendRawAsync(new JsonObject
{
    ["title"]  = "Order sync failed",
    ["status"] = "trigger",
    ["id"]     = "shopify-sync",
});
```

A plain object becomes a `JsonNode` through `JsonSerializer.SerializeToNode`,
and a JSON string through `JsonNode.Parse`. Taking the node rather than the
object is what keeps this package free of reflection, so it survives trimming
and native AOT.

## Handling failures

```csharp
try
{
    await client.SendAlertAsync(alert);
}
catch (ValidationException error)
{
    // The alert was rejected locally; ITOC360 was never contacted.
    logger.LogError("bad alert: {Field}", error.Field);
}
catch (ApiException error) when (error.Unauthorized)
{
    logger.LogError("the ITOC360 token is missing or not recognised");
}
catch (ApiException error) when (error.Retryable)
{
    // 408, 429 and 5xx are worth sending again unchanged.
    await retryQueue.EnqueueAsync(alert);
}
catch (Itoc360Exception error)
{
    // Every exception from this package derives from Itoc360Exception.
    logger.LogError(error, "could not reach ITOC360");
}
```

`ApiException` also exposes `Status`, `Detail` and `SubscriptionInactive`.
Neither the token nor any request header ever appears in an exception message.

## Configuration

```csharp
using var client = new Itoc360Client(new Itoc360ClientOptions
{
    Token      = token,
    BaseUrl    = "https://itoc360.internal",   // self-hosted deployments
    Timeout    = TimeSpan.FromSeconds(10),     // per request; default 30s
    UserAgent  = $"billing-worker/2.1 itoc360-dotnet/{Itoc360Client.Version}",
    HttpClient = httpClient,                   // your handler pipeline
});
```

A client supplied through `HttpClient` belongs to you: its lifetime, its
`Timeout` and its handler pipeline are all left as they were, and `Dispose` on
the SDK client does not dispose it. The SDK applies its own timeout per request
instead, so a client shared with the rest of your application is never
reconfigured.

An `Itoc360Client` is safe to share across concurrent calls, and sharing one is
the intended use — it holds the connection pool.

### With dependency injection

```csharp
builder.Services.AddHttpClient("itoc360");

builder.Services.AddSingleton(provider => new Itoc360Client(new Itoc360ClientOptions
{
    Token = builder.Configuration["Itoc360:Token"]!,
    HttpClient = provider.GetRequiredService<IHttpClientFactory>().CreateClient("itoc360"),
}));
```

## Cancellation

Every send takes a `CancellationToken`. A cancellation you requested surfaces as
`OperationCanceledException`; the client's own timeout surfaces as
`TransportException`, so the two are easy to tell apart.

## Documentation

[docs.itoc360.com](https://docs.itoc360.com)

## License

Apache 2.0 — see [LICENSE](LICENSE).
