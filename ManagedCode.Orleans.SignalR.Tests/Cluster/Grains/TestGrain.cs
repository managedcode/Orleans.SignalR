using ManagedCode.Orleans.SignalR.Tests.Cluster.Grains.Interfaces;
using ManagedCode.Orleans.SignalR.Tests.TestApp.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace ManagedCode.Orleans.SignalR.Tests.Cluster.Grains;

public class TestGrain :Grain, ITestGrain
{
    private readonly IHubContext<InterfaceTestHub> _hubContext;

    public TestGrain(IHubContext<InterfaceTestHub> hubContext)
    {
        _hubContext = hubContext;
    }
    

    public Task PushRandom()
    {
        return _hubContext.Clients.All.SendAsync("SendRandom", new Random().Next());
    }

    public Task PushMessage(string message)
    {
        return _hubContext.Clients.All.SendAsync("SendMessage", this.GetPrimaryKeyString());
    }

    public async Task<string> GetMessage(string connectionId)
    {
        var message = await _hubContext.Clients.Client(connectionId).InvokeAsync<string>("GetMessage", CancellationToken.None);
        return message;
    }
}