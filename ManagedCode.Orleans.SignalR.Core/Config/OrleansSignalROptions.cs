namespace ManagedCode.Orleans.SignalR.Core.Config;

public class OrleansSignalROptions
{
    public const string DefaultSignalRStreamProvider = "OrleansSignalRStreamProvider";
    public const string OrleansSignalRStorage = "OrleansSignalRStorage";
    public string StreamProvider { get; set; } = DefaultSignalRStreamProvider;
}