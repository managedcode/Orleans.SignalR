using ManagedCode.Orleans.SignalR.Core.Interfaces;
using ManagedCode.Orleans.SignalR.Core.SignalR.Observers;
using Shouldly;
using Xunit;

namespace ManagedCode.Orleans.SignalR.Tests;

public class SubscriptionTests
{
    [Fact]
    public void ConcurrentDisposeShouldDeleteObjectReferenceExactlyOnce()
    {
        using var observer = new SignalRObserver(_ => Task.CompletedTask);
        var subscription = new Subscription(observer);
        subscription.SetReference(observer);
        var deleted = 0;

        void DeleteReference(ISignalRObserver reference)
        {
            reference.ShouldBeSameAs(observer);
            Interlocked.Increment(ref deleted);
        }

        Parallel.For(0, 1_024, _ => subscription.DisposeReference(DeleteReference));
        subscription.Dispose();

        deleted.ShouldBe(1);
        observer.IsExist.ShouldBeFalse();
        subscription.Reference.ShouldBeNull();
        subscription.Grains.ShouldBeEmpty();
        subscription.GetHeartbeatGrainIds().ShouldBeEmpty();
    }
}
