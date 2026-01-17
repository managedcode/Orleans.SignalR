# Feature: Observer health and circuit breaker

## Summary

Observer delivery is protected by health tracking, circuit breaker logic, and optional grace period buffering. This prevents repeated delivery failures from cascading across fan-out operations.

## Scope

**In scope**
- Failure tracking and circuit breaker state per connection.
- Optional grace period buffering and replay.

**Out of scope**
- Partitioning strategy and routing.
- User message buffering.

## Implementation plan (step-by-step)

- [x] Clarify failure-threshold semantics so circuit-breaker and dead removal do not conflict.
- [x] Route grace-period expiration to observer cleanup and health-state removal.
- [x] Offload observer notifications from the Orleans scheduler and document why it is critical.
- [x] Add tests for circuit-breaker threshold behavior (enabled vs disabled).

## Main flow

```mermaid
flowchart TD
  Dispatch["Dispatch message"]
  Health["ObserverHealthTracker"]
  Circuit["Circuit breaker"]
  Buffer["Grace period buffer"]
  Deliver["Observer delivery"]

  Dispatch --> Health --> Circuit
  Circuit -->|Closed| Deliver
  Circuit -->|Open| Buffer
```

## Behavior notes

- Failed deliveries are recorded in a rolling failure window.
- When thresholds are exceeded, the circuit opens and delivery is skipped.
- If a grace period is configured, messages are buffered and replayed on recovery; expired grace periods remove observers.
- Without a grace period, reaching the failure threshold removes the observer immediately.

## Configuration knobs

- `OrleansSignalROptions.ObserverFailureThreshold`
- `OrleansSignalROptions.ObserverFailureWindow`
- `OrleansSignalROptions.EnableCircuitBreaker`
- `OrleansSignalROptions.CircuitBreakerOpenDuration`
- `OrleansSignalROptions.CircuitBreakerHalfOpenTestInterval`
- `OrleansSignalROptions.ObserverGracePeriod`
- `OrleansSignalROptions.MaxBufferedMessagesPerObserver`

## Key types and files

- `ManagedCode.Orleans.SignalR.Core/SignalR/Observers/ObserverHealthTracker.cs`
- `ManagedCode.Orleans.SignalR.Core/SignalR/Observers/ExpiringObserverBuffer.cs`
- `ManagedCode.Orleans.SignalR.Server/SignalRObserverGrainBase.cs`
