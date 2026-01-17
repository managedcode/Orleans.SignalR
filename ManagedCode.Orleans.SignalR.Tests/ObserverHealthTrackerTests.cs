using ManagedCode.Orleans.SignalR.Core.SignalR.Observers;
using Shouldly;
using Xunit;

namespace ManagedCode.Orleans.SignalR.Tests;

public class ObserverHealthTrackerTests
{
    [Fact]
    public void CircuitBreakerEnabled_OpensWithoutImmediateDeathAsync()
    {
        var tracker = new ObserverHealthTracker(
            failureThreshold: 2,
            failureWindow: TimeSpan.FromMinutes(1),
            circuitBreakerEnabled: true,
            circuitOpenDuration: TimeSpan.FromMilliseconds(50),
            halfOpenTestInterval: TimeSpan.FromMilliseconds(10),
            gracePeriod: TimeSpan.FromSeconds(1),
            maxBufferedMessages: 5);

        const string connectionId = "conn-1";

        tracker.RecordFailure(connectionId).ShouldBe(FailureResult.Healthy);
        tracker.RecordFailure(connectionId).ShouldBe(FailureResult.CircuitOpened);
        tracker.AllowRequest(connectionId).ShouldBeFalse();
    }

    [Fact]
    public void CircuitBreakerDisabled_MarksDeadAtThresholdAsync()
    {
        var tracker = new ObserverHealthTracker(
            failureThreshold: 2,
            failureWindow: TimeSpan.FromMinutes(1),
            circuitBreakerEnabled: false);

        const string connectionId = "conn-2";

        tracker.RecordFailure(connectionId).ShouldBe(FailureResult.Healthy);
        tracker.RecordFailure(connectionId).ShouldBe(FailureResult.Dead);
        tracker.AllowRequest(connectionId).ShouldBeFalse();
    }

    [Fact]
    public void CircuitBreakerEnabledWithoutGracePeriod_MarksDeadAtThresholdAsync()
    {
        var tracker = new ObserverHealthTracker(
            failureThreshold: 2,
            failureWindow: TimeSpan.FromMinutes(1),
            circuitBreakerEnabled: true,
            gracePeriod: TimeSpan.Zero);

        const string connectionId = "conn-3";

        tracker.RecordFailure(connectionId).ShouldBe(FailureResult.Healthy);
        tracker.RecordFailure(connectionId).ShouldBe(FailureResult.Dead);
        tracker.AllowRequest(connectionId).ShouldBeFalse();
    }
}
