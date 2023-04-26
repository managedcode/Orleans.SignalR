namespace ManagedCode.Orleans.SignalR.Core.Config;

public class OrleansSignalROptions
{
    public string StreamProvider { get; set; } = DefaultSignalRStreamProvider;
    
    public const string DefaultSignalRStreamProvider = "OrleansSignalRStreamProvider";
    public const string OrleansSignalRStorage = "OrleansSignalRStorage";
}