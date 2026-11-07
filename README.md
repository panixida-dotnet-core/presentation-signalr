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

- .NET 10 SDK and ASP.NET Core runtime.
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

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSignalR(builder.Configuration);

var app = builder.Build();
app.MapHub<EventsHub>("/hubs/events");
app.Run();

public sealed class EventsHub : Hub;
```

Without `SignalR:Redis`, SignalR uses its standard in-memory lifetime manager.
Merely configuring `ConnectionStrings:Redis` does not enable the backplane.

## Usage

### Authentication and browser clients

Configure authentication and authorization in the host, then call
`UseSignalR` on the built `WebApplication` before authentication. The extension
returns the same `WebApplication` for further middleware and endpoint mapping.
Protect each hub with `.RequireAuthorization()`.
The adapter only copies the token into a Bearer header; normal validation still
checks the token and decides access.

```csharp
app.UseSignalR("/hubs/events", "/hubs/notifications");
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.MapHub<EventsHub>("/hubs/events").RequireAuthorization();
```

With `PANiXiDA.Core.Presentation.Http`, call `UseSignalR` before
`app.UseHttp()`, which owns routing, authentication and authorization. Map hubs
afterwards. This package does not register an authentication scheme or map hubs.

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
Invalid hub paths or an empty path list fail immediately during pipeline configuration.

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
standard mapping options directly. Application-specific wrappers are unnecessary
unless they express application behavior.

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
  PANiXiDA.Core.Presentation.SignalR/
    Configurations/
    DependencyInjection/
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

The NuGet package includes the `net10.0` library, XML documentation, README,
license metadata, icon and repository metadata. Redis integration is a runtime
package dependency; SignalR Client and Testcontainers are test-only.
A symbols package is produced by the repository's packaging settings.

## License

Licensed under Apache-2.0. See [LICENSE](LICENSE).
