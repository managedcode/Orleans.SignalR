# Architecture Overview

## Scoping (read first)

**In scope**
- How the core, client, server, and test modules interact.
- Where the primary entry points live for SignalR hosts and Orleans grains.
- Dependency boundaries between modules.

**Out of scope**
- Detailed feature flows (see docs/Features/*).
- Architectural decisions and trade-offs (see docs/ADR/*).

**Entry points to start from**
- `ManagedCode.Orleans.SignalR.Core/SignalR/OrleansHubLifetimeManager.cs`
- `ManagedCode.Orleans.SignalR.Server/SignalRConnectionCoordinatorGrain.cs`
- `ManagedCode.Orleans.SignalR.Server/SignalRGroupCoordinatorGrain.cs`
- `ManagedCode.Orleans.SignalR.Server/SignalRObserverGrainBase.cs`
- `ManagedCode.Orleans.SignalR.Client/Extensions/OrleansDependencyInjectionExtensions.cs`
- `ManagedCode.Orleans.SignalR.Tests/TestApp/HttpHostProgram.cs`

## System / module map

```mermaid
flowchart LR
  Host["ASP.NET Core host"]
  Hub["SignalR hubs"]
  ClientExt["ManagedCode.Orleans.SignalR.Client"]
  Core["ManagedCode.Orleans.SignalR.Core"]
  Server["ManagedCode.Orleans.SignalR.Server"]
  Orleans["Orleans runtime"]
  Storage["Orleans storage provider (ManagedCode.Orleans.SignalR.Storage)"]
  Clients["Connected clients"]

  Host --> Hub
  Hub --> ClientExt
  ClientExt --> Core
  Core --> Server
  Server --> Orleans
  Server --> Storage
  Orleans --> Clients
```

## Module catalog (responsibilities + boundaries)

- ManagedCode.Orleans.SignalR.Core
  - Responsibilities: core abstractions, options, hub lifetime manager, hashing helpers, and shared interfaces.
  - Boundaries: must not depend on Client, Server, or Tests; keep ASP.NET Core hosting out of this module.
- ManagedCode.Orleans.SignalR.Client
  - Responsibilities: DI extensions for wiring SignalR and Orleans in ASP.NET Core hosts.
  - Boundaries: depends only on Core; no Orleans grains or server-specific plumbing.
- ManagedCode.Orleans.SignalR.Server
  - Responsibilities: Orleans grains, persistence, routing coordinators, and server-side backplane mechanics.
  - Boundaries: depends on Core; does not depend on Client or Tests.
- ManagedCode.Orleans.SignalR.Tests
  - Responsibilities: integration and reliability tests, Orleans TestCluster setup, and minimal SignalR host.
  - Boundaries: test-only; must never be referenced by production projects.

## Dependency rules (allowed/forbidden)

**Allowed**
- Client -> Core
- Server -> Core
- Tests -> Core / Client / Server / TestApp / Cluster

**Forbidden**
- Core -> Client / Server / Tests
- Client -> Server
- Server -> Client
- Production projects -> Tests

## Link index (anchors for diagram elements)

- ASP.NET Core host: `ManagedCode.Orleans.SignalR.Tests/TestApp/HttpHostProgram.cs`
- SignalR hubs: `ManagedCode.Orleans.SignalR.Tests/TestApp/Hubs/SimpleTestHub.cs`
- ManagedCode.Orleans.SignalR.Client: `ManagedCode.Orleans.SignalR.Client/ManagedCode.Orleans.SignalR.Client.csproj`
- ManagedCode.Orleans.SignalR.Core: `ManagedCode.Orleans.SignalR.Core/ManagedCode.Orleans.SignalR.Core.csproj`
- ManagedCode.Orleans.SignalR.Server: `ManagedCode.Orleans.SignalR.Server/ManagedCode.Orleans.SignalR.Server.csproj`
- Orleans runtime (package references): `Directory.Packages.props`
- Orleans storage provider key: `ManagedCode.Orleans.SignalR.Core/Config/OrleansSignalROptions.cs`
- Connected clients (test harness): `ManagedCode.Orleans.SignalR.Tests/HubSmokeTests.cs`
- SignalR metrics: `docs/Features/Diagnostics-Metrics.md`

## Key ADRs and feature specs

- ADRs
  - `docs/ADR/0001-architecture-docs-structure.md`
  - `docs/ADR/0002-orleans-hub-lifetime-manager.md`
  - `docs/ADR/0003-partitioned-routing-with-consistent-hashing.md`
  - `docs/ADR/0004-observer-health-circuit-breaker.md`
  - `docs/ADR/0005-connection-heartbeat-keepalive.md`
  - `docs/ADR/0006-persistent-state-and-storage-provider.md`
  - `docs/ADR/0007-invocation-grain-for-client-invocations.md`
  - `docs/ADR/0008-typed-orleans-hub-context.md`
  - `docs/ADR/0009-user-fanout-and-offline-buffering.md`
- Features
  - `docs/Features/Connection-Partitioning.md`
  - `docs/Features/Group-Partitioning.md`
  - `docs/Features/Diagnostics-Metrics.md`
  - `docs/Features/Hub-Lifetime-Manager-Integration.md`
  - `docs/Features/Observer-Health-and-Circuit-Breaker.md`
  - `docs/Features/Connection-Heartbeat.md`
  - `docs/Features/State-Persistence.md`
  - `docs/Features/Invocation-Routing.md`
  - `docs/Features/Typed-Hub-Context.md`
  - `docs/Features/User-Fanout-and-Offline-Buffering.md`
