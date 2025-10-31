using ManagedCode.Orleans.SignalR.Core.HubContext;
using ManagedCode.Orleans.SignalR.Tests.Cluster.Grains.Interfaces;
using ManagedCode.Orleans.SignalR.Tests.TestApp.Hubs;
using Microsoft.AspNetCore.SignalR;
using Orleans.Concurrency;

namespace ManagedCode.Orleans.SignalR.Tests.Cluster.Grains;

[Reentrant]
public class TestGrain : Grain, ITestGrain
{
    private readonly IHubContext<InterfaceTestHub> _hubContext;
    private readonly IOrleansHubContext<InterfaceTestHub, IClientInterfaceHub> _orleansHubContext;

    public TestGrain(IHubContext<InterfaceTestHub> hubContext,
        IOrleansHubContext<InterfaceTestHub, IClientInterfaceHub> orleansHubContext)
    {
        _hubContext = hubContext;
        _orleansHubContext = orleansHubContext;
    }

    public Task PushRandom()
    {
        return _hubContext.Clients.All.SendAsync("SendRandom", new Random().Next());
    }

    public Task PushMessage(string message)
    {
        return _orleansHubContext.Clients.All.SendMessage(this.GetPrimaryKeyString());
    }

    public Task<string> GetMessageInvoke(string connectionId)
    {
        return _hubContext.Clients.Client(connectionId)
            .InvokeAsync<string>("GetMessage", CancellationToken.None);
    }

    public Task<string> GetMessage(string connectionId)
    {
        return _orleansHubContext.Clients.Client(connectionId).GetMessage();
    }
    
    public Task SendToUser(string userName, string message)
    {
        return _hubContext.Clients.User(userName).SendAsync("SendMessage", message);
    }
}
