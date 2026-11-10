# PANiXiDA.Core.Presentation.SignalR

Reusable ASP.NET Core SignalR registration, optional Redis backplane configuration,
and browser access token forwarding for .NET 10.

## Status

[![CI](https://github.com/panixida-dotnet-core/presentation-signalr/actions/workflows/ci.yml/badge.svg)](https://github.com/panixida-dotnet-core/presentation-signalr/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/PANiXiDA.Core.Presentation.SignalR.svg)](https://www.nuget.org/packages/PANiXiDA.Core.Presentation.SignalR)
[![NuGet downloads](https://img.shields.io/nuget/dt/PANiXiDA.Core.Presentation.SignalR.svg)](https://www.nuget.org/packages/PANiXiDA.Core.Presentation.SignalR)
[![Target Framework](https://img.shields.io/badge/target-net10.0-512BD4)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/github/license/panixida-dotnet-core/presentation-signalr.svg)](LICENSE)

## Overview

Register the package once in the host's composition root. Applications own hubs,
client contracts, authorization policies, group names, business subscriptions,
and message delivery semantics. There is no dependency on the HTTP presentation,
Application or Mediator packages. Result mapping uses `PANiXiDA.Core.ResultPattern`.

## Features

- Standard SignalR services and builder for additional configuration.
- ResultPattern helpers using standard HubException errors and strict JSON payload contracts.
- JSON and MessagePack protocols with source-generated payload contracts for Native AOT.
- Optional Redis backplane using a named connection string and a literal channel prefix.
- Startup validation of Redis configuration without printing credentials.
- Query token forwarding restricted to explicitly registered hub transport paths.
- Existing Authorization headers and host authentication remain authoritative.

## Quick Start

### Requirements

- .NET 10 SDK with Roslyn 5.9 or later, and ASP.NET Core runtime.
- Redis when the backplane is enabled.
- Docker for integration tests.

### Installation

```bash
dotnet add package PANiXiDA.Core.Presentation.SignalR
```

### Minimal setup

```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using PANiXiDA.Core.Presentation.SignalR.DependencyInjection;
using PANiXiDA.Core.Presentation.SignalR.Hubs;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSignalR(builder.Configuration);

var app = builder.Build();
app.UseSignalR(typeof(EventsHub).Assembly);
app.Run();

public sealed class EventsHub : Hub, IHubEndpoint
{
    public static HubEndpointConventionBuilder Map(HubMapBuilder hubs) =>
        hubs.MapHub<EventsHub>("/hubs/events");
}
```

Without `SignalR:Redis`, SignalR uses its standard in-memory lifetime manager.
Merely configuring `ConnectionStrings:Redis` does not enable the backplane.

## Usage

### Authentication and browser clients

Configure authentication and authorization in the host, then call
`UseSignalR` on the built `WebApplication` before authentication. Pass one
assembly reference for each presentation module. Generated registrations map
its concrete `IHubEndpoint` implementations and supply their exact transport
paths for query tokens. The extension returns the same `WebApplication`.
Protect the hub in its static `Map` method:

```csharp
public sealed class AuthorizedEventsHub : Hub, IHubEndpoint
{
    public static HubEndpointConventionBuilder Map(HubMapBuilder hubs) =>
        hubs.MapHub<AuthorizedEventsHub>("/hubs/events").RequireAuthorization();
}
```

The adapter only copies the token into a Bearer header; normal validation still
checks the token and decides access.

```csharp
app.UseSignalR(typeof(AuthorizedEventsHub).Assembly);
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
```

With `PANiXiDA.Core.Presentation.Http`, call `UseSignalR` before
`app.UseHttp()`, which owns routing, authentication and authorization. Hubs are
mapped inside `UseSignalR`; no separate `MapHub` call is needed afterwards.
Authentication schemes and authorization policies remain host-owned.

```javascript
const connection = new signalR.HubConnectionBuilder()
  .withUrl('/hubs/events', { accessTokenFactory: () => accessToken })
  .withAutomaticReconnect()
  .build();
await connection.start();
```

Use the standard SignalR client negotiation and transport selection. Browser
WebSocket and Server-Sent Events requests can provide `access_token` in the query.
Negotiation uses normal header authentication.

Path matching is exact, case insensitive, and ignores trailing slashes. Child
routes such as `/hubs/events/negotiate`, sibling paths, and ordinary API endpoints
cannot use query tokens. Supply literal paths without query strings, route
parameters or `PathBase`; `/gateway/hubs/events` behind a `/gateway` PathBase uses
`/hubs/events`. Existing Authorization headers, including empty ones, take
precedence. Missing, blank, repeated and control-character query tokens are ignored.
Invalid or duplicate hub paths and calls that map no hubs fail immediately
during pipeline configuration. Duplicate detection ignores case and trailing
slashes. Trailing slashes are removed from mapped routes.

### Assembly discovery

Reference this package directly in every project declaring mapped hubs, with
analyzer assets enabled. The included incremental source generator emits direct
calls to each hub's static `IHubEndpoint.Map` contract and registers them through
a module initializer. Hub constructors are not invoked during discovery; SignalR
still creates hub instances through the host's DI container when needed.

`UseSignalR(params Assembly[] assemblies)` preserves assembly argument order and
maps hubs in ordinal type-name order within each assembly. Repeated assembly
arguments are ignored. Abstract and unmarked types are skipped. Accessible
internal and nested hubs are supported. Open generic, private, protected and
file-local hub types are rejected with `PANSIGNALRSG001`; mapping contracts on
non-hub types produce `PANSIGNALRSG002`.

An assembly without generated registration fails at startup with instructions
to enable the package's analyzers and rebuild. Assemblies with an empty generated
registration may accompany modules containing hubs; at least one hub must be
mapped by the call. There is no reflection discovery or activation fallback.

### Standard SignalR customization

The registration method returns `ISignalRServerBuilder`:

```csharp
builder.Services.AddSignalR(builder.Configuration)
  .AddHubOptions<EventsHub>(options =>
  {
      options.MaximumReceiveMessageSize = 32 * 1024;
  });
```

Use `Hub<TClient>`, `IHubContext<THub, TClient>`, standard group operations and
standard mapping options. Static `Map` implementations return standard
convention builders for authorization and metadata, and accept standard
connection options. For explicit registration, the callback overload remains
available and supports multiple `MapHub<THub>` calls:

```csharp
app.UseSignalR(hubs =>
    hubs.MapHub<EventsHub>("/hubs/events",
        options => options.CloseOnAuthenticationExpiration = true)
        .RequireAuthorization());
```

Generated and explicit registrations use generic hub types and preserve the members
required by SignalR trimming analysis; it does not scan assemblies at runtime.
Application-specific publishers and subscriptions remain in the application.

## Result mapping

Call the helpers explicitly in hub methods:

```csharp
using Microsoft.AspNetCore.SignalR;
using PANiXiDA.Core.Presentation.SignalR.Helpers;
using PANiXiDA.Core.ResultPattern;

public sealed class ResultsHub : Hub
{
    public void Complete() => Result.Success().ThrowIfFailure();
    public int GetCount() => Result.Success(0).GetValueOrThrow();
}
```

Success returns an ordinary value/DTO or no value. Failure maps only the first
error to `HubException`: expected errors use its message; unexpected/unknown use
`An unexpected error occurred.` Additional errors and metadata are not sent.
Use ResultPattern `Map` first when converting an application value to a response.

`EnableDetailedErrors` defaults to true only in Development, based on
`IHostEnvironment`; without an environment it is false. A later
`Configure<HubOptions>` can override it. Expected HubException messages reach
clients in every environment.

## JSON contracts

JSON uses `NumberHandling.Strict`, `RespectRequiredConstructorParameters = true`,
and `RespectNullableAnnotations = true`, independently of HTTP and MessagePack.
Missing constructor arguments without defaults (including nullable ones) and
explicit nulls for non-nullable members are rejected. Invalid non-nullable output
also fails serialization. Collection-element and generic nullability are not checked.

Customize JSON through `AddJsonProtocol`; existing resolvers and converters are
preserved. Native AOT requires a `JsonSerializerContext` covering all payload
types, as in the combined example below.

## MessagePack contracts

Both protocols are enabled; the client selects one at handshake, without automatic
fallback. MessagePack uses the AOT-compatible `Nerdbank.MessagePack.SignalR` and is
tested with Microsoft's client. Defaults use CLR member names, enum names, GUID
strings (`D`), invariant decimal strings, and preserved DateTimeOffset offsets.
Custom contracts and serializer settings must match the client.

The default registration supplies common scalar shapes. For DTOs, enums,
collections, stream items, and notifications, pass a generated provider containing
all top-level payload types. It replaces the built-in provider; member shapes are
generated transitively. Example configuring both protocols for Native AOT:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PANiXiDA.Core.Presentation.SignalR.DependencyInjection;
using PolyType;

builder.Services.AddSignalR(builder.Configuration,
        HubMessagePackTypes.GeneratedTypeShapeProvider)
    .AddJsonProtocol(options =>
        options.PayloadSerializerOptions.TypeInfoResolverChain.Insert(0, HubJsonContext.Default));

public sealed record CountResponse(int Count);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(CountResponse))]
internal partial class HubJsonContext : JsonSerializerContext;

[GenerateShapeFor<int>]
[GenerateShapeFor<CountResponse>]
internal partial class HubMessagePackTypes;
```

Unknown MessagePack contracts fail without reflection fallback. Redis publishers
need shapes for every notification DTO even with JSON-only receivers, because the
backplane serializes all enabled protocols. The registration overload also accepts
an optional `Nerdbank.MessagePack.MessagePackSerializer` for host customization.

## Logging

`AddSignalR(configuration)` installs a global `IHubFilter` for client-invoked hub
methods and the `OnConnectedAsync` / `OnDisconnectedAsync` callbacks. Logs written
inside these operations inherit snapshot scopes with `rpc.system`, `rpc.service`,
`rpc.method`, `signalr.connection.id`, and `enduser.id`. The user ID is SignalR's
`UserIdentifier`, including a host-provided `IUserIdProvider`; it may be null.

Each operation writes one `Information` event, `SignalR operation finished`, with
`signalr.operation.duration_ms` and `signalr.operation.outcome` (`succeeded`,
`failed`, or `cancelled`). Cancellation is classified as `cancelled` only when
the connection cancellation token is signalled. A disconnect with a transport
exception is reported as `failed`. Exceptions propagate unchanged, and their
details remain owned by the standard SignalR logger to avoid duplicate error
logs. Normal logging filters apply to
`PANiXiDA.Core.Presentation.SignalR.Logging.HubLoggingFilter`.

The filter does not log arguments, return values, tokens, or notification payloads.
Outgoing notifications through `IHubContext` are not intercepted. For streaming
methods the duration measures the method invocation, not subsequent stream
enumeration. The filter does not create activities; OpenTelemetry can collect
SignalR's built-in activities independently. HTTP request and proxy logs still
need their own query token redaction.

## Configuration

Enable Redis with the `SignalR:Redis` section:

```json
{
  "ConnectionStrings": {
    "Redis": "localhost:6379"
  },
  "SignalR": {
    "Redis": {
      "ConnectionStringName": "Redis",
      "ChannelPrefix": "my-application:development"
    }
  }
}
```

`ConnectionStringName` defaults to `Redis`. When the section exists, a nonblank
connection string with a Redis endpoint and a nonblank `ChannelPrefix` are
required. Invalid settings fail during application startup. Validation checks
configuration syntax, not server availability. Settings require an application
restart; configure Redis transport details such as TLS, credentials and timeouts
in the connection string. Supply production credentials through the host's secret
store, for example `ConnectionStrings__Redis`.

All replicas of one application/environment must use the same channel prefix.
Different applications and environments must use different prefixes to isolate
messages on a shared Redis server. All hubs registered in one host share the
configured backplane.

### Hosting and delivery

- Run Redis close to the API replicas and allow the necessary Pub/Sub commands.
- Configure load balancer session affinity for standard negotiated connections.
- Configure host CORS policies for browser clients on other origins.
- Use HTTPS and redact `access_token` from request and proxy logs.
- Redis backplane does not persist or replay messages during an outage.
- Reconnects and initial snapshots remain application responsibilities.
- An established connection does not automatically revalidate token revocation.
  Use the standard `CloseOnAuthenticationExpiration` hub mapping option when
  expiry should close a connection; immediate revocation needs host-specific handling.

See Microsoft's [Redis backplane guidance](https://learn.microsoft.com/aspnet/core/signalr/redis-backplane?view=aspnetcore-10.0)
and [SignalR authentication guidance](https://learn.microsoft.com/aspnet/core/signalr/authn-and-authz?view=aspnetcore-10.0).

### Native AOT

The library enables `IsAotCompatible` and `EnableConfigurationBindingGenerator`.
AOT/trimming analyzers run during library builds, and Redis options binding uses
generated code. These settings do not enable Native AOT publishing in consuming
applications; the host controls `PublishAot` and must validate its own dependencies
and hub implementations.

For Native AOT hosts, use source-generated contracts for each enabled protocol:
register a `JsonSerializerContext` in
`JsonHubProtocolOptions.PayloadSerializerOptions.TypeInfoResolverChain` for JSON
and pass a generated type shape provider for MessagePack as shown above. Use
ordinary `Hub` and `IHubContext<THub>`: strongly typed `Hub<TClient>` and
`IHubContext<THub, TClient>` require runtime proxy generation and are not supported
by Native AOT. They remain available for normal JIT deployments.

See Microsoft's [SignalR Native AOT guidance](https://learn.microsoft.com/aspnet/core/release-notes/aspnetcore-9.0#signalr-supports-trimming-and-native-aot)
and [configuration source generator documentation](https://learn.microsoft.com/dotnet/core/extensions/configuration-generator).

## Project Structure

```text
src/
  PANiXiDA.Core.Presentation.SignalR.Generators/
  PANiXiDA.Core.Presentation.SignalR/
    Configurations/
    DependencyInjection/
    Hubs/
tests/
  PANiXiDA.Core.Presentation.SignalR.UnitTests/
  PANiXiDA.Core.Presentation.SignalR.IntegrationTests/
```

## AsyncAPI documentation

Mark a hub (or its assembly) with the shared module/version metadata:

```csharp
using PANiXiDA.Core.ApiDocumentation;

[ApiContract("matchmaking", "1.0", Title = "Matchmaking")]
public sealed class MatchmakingHub : Hub, IHubEndpoint
{
    public static HubEndpointConventionBuilder Map(HubMapBuilder hubs) =>
        hubs.MapHub<MatchmakingHub>("/hubs/v1/matchmaking").RequireAuthorization();

    /// <summary>Returns current search state.</summary>
    public Task<SearchState> GetState(Guid searchId) => Task.FromResult(new SearchState(searchId));
}
```

The example assumes an application DTO such as `public sealed record SearchState(Guid SearchId);`.

The generator discovers public hub methods, inherited methods, `HubMethodName` targets, typed `Hub<TClient>` callbacks, `Task`/`ValueTask` results, and `IAsyncEnumerable<T>`/`ChannelReader<T>` streams. XML summaries are extracted at compile time. DI parameters, streaming cancellation tokens and trailing typed-callback cancellation tokens are excluded from serialized arguments. Return streams include implemented interfaces and inherited channel readers. Overloaded targets or generic methods produce a build diagnostic for documented hubs.

Register source-generated JSON metadata for every argument, result and stream item, then use the independent presenter in the host:

```csharp
builder.Services.AddSignalR(builder.Configuration);
builder.Services.AddApiDocumentation(builder.Configuration, ApplicationJsonContext.Default);
var app = builder.Build();
app.UseSignalR(typeof(MatchmakingHub).Assembly);
app.UseApiDocumentation();
```

The last two calls require `PANiXiDA.Core.Presentation.Scalar.DependencyInjection` and the existing SignalR dependency injection namespace. `ApplicationJsonContext` is an application-owned `[JsonSerializable]` context. It supplies schemas; configure the same contracts for SignalR JSON serialization and MessagePack shapes as usual. Missing schema metadata fails explicitly without reflection fallback.

`/asyncapi/matchmaking-signalr-v1.json` contains the hub's actual mapped path, invocation arguments, return values, callbacks and stream items. `x-signalr` bindings identify targets, streams, enabled protocols, mapped transports and authorization requirements. JSON invocation, completion and stream-item envelopes include generated DTO schemas; completion errors remain strings. MessagePack uses the same application payload types with its binary protocol encoding. Errors still use SignalR completion errors and `HubException`.

Each module/version gets a separate document. Use separate hub types/routes for independently deployed versions; `ApiContract` documents versioning and does not rewrite routes or negotiate versions. Unmarked hubs keep their existing behavior and do not produce documents. Untyped `Clients.SendAsync` target names cannot be inferred statically; use an explicit document source for such notifications.

Native AOT documentation uses generated hub metadata and JSON contexts. CI publishes and runs a real Kestrel host with a plain `Hub`. ASP.NET Core's `Hub<TClient>` proxy generation and value-type upload streams retain their platform AOT restrictions; documenting typed callbacks does not remove those restrictions.

## Development

```bash
dotnet restore
dotnet format
dotnet build --configuration Release
dotnet test --configuration Release
dotnet pack --configuration Release
```

Hosting tests start Kestrel on `127.0.0.1` with an OS-assigned port and use real
HTTP, WebSocket and Long Polling connections. Authentication uses a test handler.
Integration tests start an isolated Redis container with Testcontainers and clean
it up afterwards. They exercise delivery between independent application hosts.
To run only the tests that do not require Docker:

```bash
dotnet test --project tests/PANiXiDA.Core.Presentation.SignalR.UnitTests/PANiXiDA.Core.Presentation.SignalR.UnitTests.csproj --configuration Release
```

### Continuous integration

Every pull request and push to `main` runs formatting, tests, and mandatory
SonarQube analysis. Publishing starts only after formatting, tests and the
SonarQube Quality Gate succeed. Versioning uses Nerdbank.GitVersioning and tracks
package source, shared build settings, README and icon.

## Package Contents

The NuGet package includes the `net10.0` library, its `netstandard2.0` source
generator under `analyzers/dotnet/cs`, XML documentation, README,
license metadata, icon and repository metadata. Redis integration,
`PANiXiDA.Core.ResultPattern`, and `Nerdbank.MessagePack.SignalR` are runtime
package dependencies. PolyType supplies the MessagePack type shape generator;
Microsoft's SignalR Client, MessagePack protocol, and Testcontainers are test-only.
A symbols package is produced by the repository's packaging settings.

## License

Licensed under Apache-2.0. See [LICENSE](LICENSE).
