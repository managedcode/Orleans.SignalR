# ADR-0007: Invocation grain for client invocations

Date: 2026-01-17
Status: Accepted

## Context

SignalR client invocations require correlating an invocation ID with a completion message. This state must survive grain reactivation and avoid blocking Orleans activation threads.

## Decision

Use a dedicated `SignalRInvocationGrain` per invocation ID. The grain stores invocation metadata, subscribes observers for completion, and exposes `WaitForCompletion` to await a completion message asynchronously. State is persisted and cleared on completion or deactivation.

## Consequences

- Each invocation becomes a short-lived grain activation.
- Completion delivery is isolated per invocation, simplifying correlation.
- Invocation state survives transient activation changes.

## Decision diagram

```mermaid
flowchart TD
  Hub["Hub lifetime manager"]
  Inv["SignalRInvocationGrain"]
  Obs["Observer"]
  Done["Completion message"]

  Hub --> Inv --> Obs --> Done
```

## Implementation plan (step-by-step)

- [x] Implement `SignalRInvocationGrain` with observer tracking and completion handling.
- [x] Persist invocation info and clear it on completion.
- [x] Expose `WaitForCompletion` for async completion flow.

## References

- `ManagedCode.Orleans.SignalR.Server/SignalRInvocationGrain.cs`
- `ManagedCode.Orleans.SignalR.Core/Interfaces/ISignalRInvocationGrain.cs`
- `ManagedCode.Orleans.SignalR.Core/Models/InvocationInfo.cs`
