using Orleans;

namespace ManagedCode.Orleans.SignalR.Core.Models;

[GenerateSerializer]
public sealed class ConnectionHeartbeatState
{
    [Id(0)]
    public ConnectionHeartbeatRegistration? Registration { get; set; }
}
