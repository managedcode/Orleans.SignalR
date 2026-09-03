using ManagedCode.Orleans.SignalR.Server;
using Shouldly;
using Xunit;

namespace ManagedCode.Orleans.SignalR.Tests;

public class ConnectionHeartbeatTests
{
    [Fact]
    public void HeartbeatTimerShouldKeepActivationAlive()
    {
        var interval = TimeSpan.FromSeconds(5);

        var options = SignalRConnectionHeartbeatGrain.CreateTimerOptions(interval);

        options.DueTime.ShouldBe(interval);
        options.Period.ShouldBe(interval);
        options.Interleave.ShouldBeTrue();
        options.KeepAlive.ShouldBeTrue();
    }
}
