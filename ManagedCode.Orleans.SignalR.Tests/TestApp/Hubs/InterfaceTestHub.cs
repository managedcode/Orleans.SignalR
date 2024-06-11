using Microsoft.AspNetCore.SignalR;

namespace ManagedCode.Orleans.SignalR.Tests.TestApp.Hubs;

public class InterfaceTestHub : Hub<IClientInterfaceHub>, IServerInterfaceHub
{
    public override Task OnConnectedAsync()
    {
        var user = Context.User;
        return base.OnConnectedAsync();
    }

    public Task PushRandom()
    {
        return Clients.All.SendRandom(new Random().Next());
    }

    public Task PushMessage(string message)
    {
        return Clients.All.SendMessage(message);
    }

    public async Task<string> WaitForMessage(string connectionId)
    {
        var message = await Clients.Client(connectionId).GetMessage();
        return message;
    }
}