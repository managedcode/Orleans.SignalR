namespace ManagedCode.Orleans.SignalR.Tests;

internal static class TestDefaults
{
    public static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan ClientTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan MessageRetention = TimeSpan.FromSeconds(6);
    public const int ConnectionPartitions = 4;
    public const int GroupPartitions = 4;
    public const int ConnectionsPerPartitionHint = 8;
    public const int GroupsPerPartitionHint = 16;
}
