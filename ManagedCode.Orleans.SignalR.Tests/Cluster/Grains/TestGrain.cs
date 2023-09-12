using ManagedCode.Orleans.SignalR.Core.HubContext;
using ManagedCode.Orleans.SignalR.Tests.Cluster.Grains.Interfaces;
using ManagedCode.Orleans.SignalR.Tests.TestApp.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace ManagedCode.Orleans.SignalR.Tests.Cluster.Grains;

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

    public async Task<string> GetMessageInvoke(string connectionId)
    {
        var localConnection = connectionId;
        var message = await Task.Run(() => _hubContext.Clients.Client(localConnection)
            .InvokeAsync<string>("GetMessage", CancellationToken.None));

        return message;
    }

    public async Task<string> GetMessage(string connectionId)
    {
        var message = await Task.Run(() => _orleansHubContext.Clients.Client(connectionId).GetMessage());
        return message;
    }
    
    public Task SendToUser(string userName, string message)
    {
        return Task.Run(() => _hubContext.Clients.User(userName).SendAsync("SendMessage", message));
    }
}