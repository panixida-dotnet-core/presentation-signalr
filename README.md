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

Handle application results explicitly with `ThrowIfFailure()` or `GetValueOrThrow()`.
Use the existing
ResultPattern `Map` extension first when the presentation response differs from
the application value:

```csharp
using PANiXiDA.Core.Presentation.SignalR.Helpers;
using PANiXiDA.Core.ResultPattern;
using Microsoft.AspNetCore.SignalR;

public sealed class ResultsHub : Hub
{
    public void Complete() => Result.Success().ThrowIfFailure();
    public int GetCount() => Result.Success(0).GetValueOrThrow();
}
```

On success, `ThrowIfFailure()` returns normally and `GetValueOrThrow()` returns
the original value, including zero, false, and an allowed null. The hub returns
an ordinary value/DTO or completes without a value. No response envelope or
client-side Result model is required.

On failure, both helpers throw the standard `HubException` before reading a
failed result's `Value`. The first error supplies the message for Validation,
NotFound, Conflict, Unauthorized, Forbidden, or Failure. Unexpected and unknown
categories use `An unexpected error occurred.` Additional errors, field metadata,
arbitrary metadata, and internal exception details are not transmitted.

Clients use the standard `invoke` / `InvokeAsync` error path: .NET clients receive
`HubException`; JavaScript clients receive a rejected promise with an `Error`.
There are no structured error categories or HTTP status codes for these RPC
failures. The helpers do not catch unrelated exceptions or change cancellation,
authorization, connection lifecycle callbacks, or outgoing notification
contracts. They are not invoked automatically by a filter.

`AddSignalR(configuration)` also installs a hub filter that unwraps a
`TargetInvocationException` containing `HubException`, as produced by synchronous
Native AOT reflection dispatch. The original HubException and its stack trace are
preserved; other exceptions propagate unchanged.

## JSON contracts

`AddSignalR(configuration)` configures `JsonHubProtocolOptions.PayloadSerializerOptions`
with the same defaults as the HTTP presentation package:

- `NumberHandling = JsonNumberHandling.Strict`;
- `RespectRequiredConstructorParameters = true`;
- `RespectNullableAnnotations = true`.

These settings apply to JSON hub arguments and return values, independently of
HTTP JSON options. Previously accepted quoted numbers, missing non-optional
constructor parameters (including nullable ones), and null non-nullable members
are rejected. An explicit null is valid for a nullable member. Optional constructor
parameters can be omitted. Collection elements, generic-member nullability, and
business validation remain application responsibilities.

The host can further configure the standard `AddJsonProtocol` callback. Its
resolver chain, converters, naming policy, and other settings are preserved.
These settings do not apply to MessagePack payloads.

For Native AOT, register source-generated metadata for the actual method argument
and return types. For example:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(CountResponse))]
internal partial class HubJsonContext : JsonSerializerContext;

public sealed record CountResponse(int Count);
```

```csharp
builder.Services.AddSignalR(builder.Configuration)
    .AddJsonProtocol(options =>
        options.PayloadSerializerOptions.TypeInfoResolverChain.Insert(0, HubJsonContext.Default));
```

Add application request/response DTOs and other RPC argument/return types to that
context when the application uses them. Configure .NET clients with their own
source-generated context as needed. The helpers use no reflection, dynamic
generic construction, or custom serializer converter.

## MessagePack contracts

`AddSignalR(configuration)` enables both JSON and binary MessagePack. The client
chooses one protocol during the SignalR handshake; registration does not force
existing JSON clients to switch or add automatic protocol fallback.

MessagePack uses `Nerdbank.MessagePack.SignalR` because its implementation supports
Native AOT without runtime code generation. It implements the standard SignalR
MessagePack wire protocol and is tested with Microsoft's MessagePack client.
Enums are serialized by name, GUIDs use the `D` string format, decimals use
invariant strings, and date offsets preserve their wall-clock timestamp and
offset. These defaults match Microsoft's SignalR MessagePack client formats.
DTOs use maps keyed by CLR member names. JSON naming settings do not affect them;
custom indexed/key-based contracts must be configured consistently on both ends.

The existing registration overload supplies generated shapes for `bool`, numeric
primitives, `decimal`, `char`, `string`, `Guid`, `DateTime`, `DateTimeOffset`,
`TimeSpan`, and `byte[]`. For application DTOs, enums, collections, and other
payloads, pass a source-generated provider covering every argument, result,
stream item, and outgoing notification payload type:

```csharp
using PolyType;
using PANiXiDA.Core.Presentation.SignalR.DependencyInjection;

builder.Services.AddSignalR(builder.Configuration,
    HubMessagePackTypes.GeneratedTypeShapeProvider);

[GenerateShapeFor<int>]
[GenerateShapeFor<bool>]
[GenerateShapeFor<string>]
[GenerateShapeFor<CountResponse>]
internal partial class HubMessagePackTypes;
```

The application provider replaces the built-in scalar provider. Include all used
top-level payload types; shapes for their members are generated transitively.
Unknown contracts fail instead of falling back to reflection. DTOs do not require
MessagePack attributes or changes to hub method signatures. JSON clients continue
to use the JSON configuration and, for AOT, their `JsonSerializerContext`.
Redis backplane publishers serialize notifications for all enabled hub protocols;
register MessagePack shapes for their DTOs even when current receivers use JSON.

The overload also accepts an optional `Nerdbank.MessagePack.MessagePackSerializer`
for application-specific serialization settings. Such settings and matching
client contracts remain the host's responsibility. JSON's strict constructor and
nullable checks are independent of MessagePack; business validation belongs in
the application for both protocols.

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
