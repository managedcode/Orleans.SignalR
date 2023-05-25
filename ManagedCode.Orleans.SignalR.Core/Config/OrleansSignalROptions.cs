using System;

namespace ManagedCode.Orleans.SignalR.Core.Config;

public class OrleansSignalROptions
{
    public const string DefaultSignalRStreamProvider = "OrleansSignalRStreamProvider";
    public const string OrleansSignalRStorage = "OrleansSignalRStorage";

    /// <summary>
    /// Gets or sets the time window clients have to send a message before the server closes the connection. The default timeout is 30 seconds.
    /// </summary>
    public TimeSpan? ClientTimeoutInterval { get; set; } = TimeSpan.FromSeconds(30);
}