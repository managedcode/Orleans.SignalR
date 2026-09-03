using ManagedCode.Orleans.SignalR.Core.SignalR.Observers;
using Microsoft.AspNetCore.SignalR.Protocol;
using Shouldly;
using Xunit;

namespace ManagedCode.Orleans.SignalR.Tests;

public sealed class SignalRObserverTests
{
    [Fact]
    public async Task ActualWriteFailureAndFirstRecoveryAreReportedAsync()
    {
        var shouldFail = true;
        var failures = 0;
        var recoveries = 0;
        var source = GrainId.Create("test", "failed-source");
        var otherSource = GrainId.Create("test", "healthy-source");
        using var observer = new SignalRObserver(
            _ => shouldFail
                ? Task.FromException(new IOException("write failed"))
                : Task.CompletedTask,
            (reportedSource, exception) =>
            {
                reportedSource.ShouldBe(source);
                exception.Message.ShouldBe("write failed");
                Interlocked.Increment(ref failures);
                return Task.CompletedTask;
            },
            reportedSource =>
            {
                reportedSource.ShouldBe(source);
                Interlocked.Increment(ref recoveries);
                return Task.CompletedTask;
            });
        var message = new InvocationMessage("observer-feedback", Array.Empty<object?>());

        await Should.ThrowAsync<IOException>(() => observer.OnNextWithDeliverySourceAsync(message, source));
        await Should.ThrowAsync<IOException>(() => observer.OnNextWithDeliverySourceAsync(message, source));

        shouldFail = false;
        await observer.OnNextWithDeliverySourceAsync(message, otherSource);
        recoveries.ShouldBe(0, "A success from another source must not close the failed source's circuit.");
        await observer.OnNextWithDeliverySourceAsync(message, source);
        await observer.OnNextWithDeliverySourceAsync(message, source);

        failures.ShouldBe(2);
        recoveries.ShouldBe(1);
    }

    [Fact]
    public async Task OfflineDeliveryIsAcknowledgedOnlyAfterWriteSucceedsAsync()
    {
        var shouldFail = true;
        var acknowledgements = new List<(Guid DeliveryId, string GrainKey)>();
        using var observer = new SignalRObserver(
            _ => shouldFail
                ? Task.FromException(new IOException("write failed"))
                : Task.CompletedTask,
            onDeliveryAcknowledged: (deliveryId, grainKey) =>
            {
                acknowledgements.Add((deliveryId, grainKey));
                return Task.CompletedTask;
            });
        var message = new InvocationMessage("offline-ack", Array.Empty<object?>());
        var deliveryId = Guid.NewGuid();

        await Should.ThrowAsync<IOException>(() =>
            observer.OnNextWithAcknowledgementAsync(message, deliveryId, "v2:user", default));
        acknowledgements.ShouldBeEmpty();

        shouldFail = false;
        await observer.OnNextWithAcknowledgementAsync(message, deliveryId, "v2:user", default);

        acknowledgements.ShouldBe([(deliveryId, "v2:user")]);
    }
}
