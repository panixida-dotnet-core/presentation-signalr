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
Application, Mediator, or Result packages.

## Features

- Standard SignalR services and builder for additional configuration.
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

For Native AOT hosts, use the JSON protocol with a source-generated
`JsonSerializerContext` covering every hub payload type. Register that context in
`JsonHubProtocolOptions.PayloadSerializerOptions.TypeInfoResolverChain`. Use
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
license metadata, icon and repository metadata. Redis integration is a runtime
package dependency; SignalR Client and Testcontainers are test-only.
A symbols package is produced by the repository's packaging settings.

## License

Licensed under Apache-2.0. See [LICENSE](LICENSE).
