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
    
    /// <summary>
    ///     Number of partitions to use for connection distribution.
    ///     Set to 1 to disable partitioning.
    ///     Increase this value for better scalability with millions of connections.
    ///     The default value is 4.
    /// </summary>
    public uint ConnectionPartitionCount { get; set; } = 4;
    
    /// <summary>
    ///     Target number of concurrent connections per partition.
    ///     Used as a hint when determining how many partitions to allocate dynamically.
    ///     Lower values result in more partitions.
    /// </summary>
    public int ConnectionsPerPartitionHint { get; set; } = 10_000;
    
    /// <summary>
    ///     Number of partitions to use for group distribution.
    ///     Set to 1 to disable partitioning.
    ///     Increase this value for better scalability with millions of groups.
    ///     The default value is 4.
    /// </summary>
    public uint GroupPartitionCount { get; set; } = 4;

    /// <summary>
    ///     Target number of groups per partition.
    ///     Used as a hint when determining how many partitions to allocate dynamically.
    /// </summary>
    public int GroupsPerPartitionHint { get; set; } = 1_000;
}
