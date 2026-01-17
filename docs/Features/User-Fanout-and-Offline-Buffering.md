# Feature: User fan-out and offline buffering

## Summary

User-targeted messages are routed through a user grain that tracks all active connections and optionally buffers messages when the user is offline.

## Scope

**In scope**
- User connection tracking and fan-out.
- Offline message buffering and replay.

**Out of scope**
- Group and connection partitioning.
- Observer health and circuit breaker behavior.

## Main flow

```mermaid
flowchart TD
  Hub["Hub user send"]
  User["SignalRUserGrain"]
  Live["Live observers"]
  Buffer["Offline buffer"]

  Hub --> User
  User --> Live
  User --> Buffer
```

## Behavior notes

- When live observers exist, messages are delivered immediately.
- When no observers exist, messages are queued with expiration and a maximum size; oldest messages are dropped if limits are exceeded.
- On reconnect, the user grain replays buffered messages via `RequestMessage`.

## Configuration knobs

- `OrleansSignalROptions.KeepMessageInterval`
- `OrleansSignalROptions.MaxQueuedMessagesPerUser`

## Key types and files

- `ManagedCode.Orleans.SignalR.Server/SignalRUserGrain.cs`
- `ManagedCode.Orleans.SignalR.Core/Models/HubMessageState.cs`
- `ManagedCode.Orleans.SignalR.Core/Config/OrleansSignalROptions.cs`
