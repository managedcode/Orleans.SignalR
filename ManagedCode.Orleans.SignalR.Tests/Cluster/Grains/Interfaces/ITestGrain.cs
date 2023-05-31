namespace ManagedCode.Orleans.SignalR.Tests.Cluster.Grains.Interfaces;

public interface ITestGrain : IGrainWithStringKey
{
    Task PushRandom();
    Task PushMessage(string message);
    Task<string> GetMessage(string connectionId);
    Task<string> GetMessageInvoke(string connectionId);
    Task SendToUser(string userName, string message);
}