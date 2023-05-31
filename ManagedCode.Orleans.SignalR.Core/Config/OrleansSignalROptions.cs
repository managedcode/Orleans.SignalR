using System;

namespace ManagedCode.Orleans.SignalR.Core.Config;

public class OrleansSignalROptions
{
    public const string OrleansSignalRStorage = "OrleansSignalRStorage";

    /// <summary>
    ///     Gets or sets the time window clients have to send a message before the server closes the connection.
    ///     The default timeout is 30 seconds.
    /// </summary>
    public TimeSpan ClientTimeoutInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    ///     If true each connection should be kept alive by sending a message to the orleans every
    ///     <see cref="HubOptions.KeepAliveInterval" />.
    ///     The default value is true.
    ///     Set to false only if you don't want to send messages to the specific connectionId.
    /// </summary>
    public bool KeepEachConnectionAlive { get; set; } = true;
    
    /// <summary>
    ///     This property determines the duration for which messages are stored when a client is disconnected.
    ///     The default timeout is 1.1 minute.
    /// </summary>
    public TimeSpan KeepMessageInterval { get; set; } = TimeSpan.FromMinutes(1.1);
}