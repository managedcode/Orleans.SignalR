namespace ManagedCode.Orleans.SignalR.Tests.TestApp.Hubs;

public interface IClientInterfaceHub
{
    Task SendRandom(int random);
    Task SendMessage(string message);
    Task<string> GetMessage();
}
