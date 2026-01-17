# Feature: State persistence and safe writes

## Summary

SignalR grains persist routing state and queued messages using a shared storage provider name and safe write helpers that retry on ETag conflicts.

## Scope

**In scope**
- Persistence for connection, group, user, and invocation grains.
- Safe write/clear helpers to handle ETag conflicts.

**Out of scope**
- Selection of specific Orleans storage providers.
- Partitioning strategy details.

## Main flow

```mermaid
flowchart TD
  Grain["SignalR grain"]
  State["IPersistentState<T>"]
  Safe["WriteStateSafeAsync"]
  Store["Storage provider"]

  Grain --> State --> Safe --> Store
```

## Behavior notes

- All SignalR grains use `OrleansSignalROptions.OrleansSignalRStorage` as the storage provider name.
- Safe write helpers retry on `InconsistentStateException` and memory storage ETag mismatch errors.

## Configuration knobs

- `OrleansSignalROptions.OrleansSignalRStorage`

## Key types and files

- `ManagedCode.Orleans.SignalR.Server/Helpers/PersistentStateExtensions.cs`
- `ManagedCode.Orleans.SignalR.Core/Config/OrleansSignalROptions.cs`
