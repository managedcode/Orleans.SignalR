namespace ManagedCode.Orleans.SignalR.Tests.TestApp.Hubs;

public interface IServerInterfaceHub
{
    Task PushRandom();
    Task PushMessage(string message);
    Task<string> WaitForMessage(string connectionId);
}