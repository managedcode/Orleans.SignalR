using System;
using System.Text;
using ManagedCode.Orleans.SignalR.Core.Interfaces;
using Orleans;
using System.IO.Hashing;

namespace ManagedCode.Orleans.SignalR.Core.SignalR;

public static class NameHelperGenerator
{
    private static string ConnectionNamespace<TMessage>(string hub)
    {
        return $"{hub}.{typeof(TMessage).FullName}";
    }

    public static ISignalRConnectionHolderGrain GetConnectionHolderGrain<THub>(IGrainFactory grainFactory)
    {
        return grainFactory.GetGrain<ISignalRConnectionHolderGrain>(CleanString(typeof(THub).FullName!));
    }

    public static ISignalRConnectionHolderGrain GetConnectionHolderGrain(IGrainFactory grainFactory, string hubKey)
    {
        return grainFactory.GetGrain<ISignalRConnectionHolderGrain>(CleanString(hubKey));
    }
    
    public static ISignalRConnectionCoordinatorGrain GetConnectionCoordinatorGrain<THub>(IGrainFactory grainFactory)
    {
        return grainFactory.GetGrain<ISignalRConnectionCoordinatorGrain>(CleanString(typeof(THub).FullName!));
    }
    
    public static ISignalRConnectionPartitionGrain GetConnectionPartitionGrain<THub>(IGrainFactory grainFactory, int partitionId)
    {
        var key = GetPartitionGrainKey(typeof(THub).FullName!, partitionId, alreadyCleaned: false);
        return grainFactory.GetGrain<ISignalRConnectionPartitionGrain>(key);
    }

    public static ISignalRConnectionPartitionGrain GetConnectionPartitionGrain(IGrainFactory grainFactory, string hubKey, int partitionId)
    {
        var key = GetPartitionGrainKey(hubKey, partitionId, alreadyCleaned: true);
        return grainFactory.GetGrain<ISignalRConnectionPartitionGrain>(key);
    }

    public static ISignalRInvocationGrain GetInvocationGrain<THub>(IGrainFactory grainFactory, string? invocationId)
    {
        return grainFactory.GetGrain<ISignalRInvocationGrain>(CleanString(typeof(THub).FullName + "::" + invocationId ?? "unknown"));
    }

    // public static ISignalRGroupHolderGrain GetGroupHolderGrain<THub>(IGrainFactory grainFactory)
    // {
    //     return grainFactory.GetGrain<ISignalRGroupHolderGrain>(typeof(THub).FullName);
    // }

    public static ISignalRUserGrain GetSignalRUserGrain<THub>(IGrainFactory grainFactory, string userId)
    {
        return grainFactory.GetGrain<ISignalRUserGrain>(CleanString(typeof(THub).FullName + "::" + userId));
    }

    public static ISignalRGroupGrain GetSignalRGroupGrain<THub>(IGrainFactory grainFactory, string groupId)
    {
        return grainFactory.GetGrain<ISignalRGroupGrain>(CleanString(typeof(THub).FullName + "::" + groupId));
    }
    
    public static ISignalRGroupCoordinatorGrain GetGroupCoordinatorGrain<THub>(IGrainFactory grainFactory)
    {
        return grainFactory.GetGrain<ISignalRGroupCoordinatorGrain>(CleanString(typeof(THub).FullName!));
    }
    
    public static ISignalRGroupCoordinatorGrain GetGroupCoordinatorGrain(IGrainFactory grainFactory, string hubKey)
    {
        return grainFactory.GetGrain<ISignalRGroupCoordinatorGrain>(CleanString(hubKey));
    }
    
    public static ISignalRGroupPartitionGrain GetGroupPartitionGrain<THub>(IGrainFactory grainFactory, int partitionId)
    {
        var key = GetPartitionGrainKey(typeof(THub).FullName!, partitionId, alreadyCleaned: false);
        return grainFactory.GetGrain<ISignalRGroupPartitionGrain>(key);
    }

    public static ISignalRGroupPartitionGrain GetGroupPartitionGrain(IGrainFactory grainFactory, string hubKey, int partitionId)
    {
        var key = GetPartitionGrainKey(hubKey, partitionId, alreadyCleaned: true);
        return grainFactory.GetGrain<ISignalRGroupPartitionGrain>(key);
    }

    public static ISignalRConnectionHeartbeatGrain GetConnectionHeartbeatGrain(IGrainFactory grainFactory, string hubKey, string connectionId)
    {
        var normalizedConnection = CleanString(connectionId);
        var key = $"{CleanString(hubKey)}::{normalizedConnection}";
        return grainFactory.GetGrain<ISignalRConnectionHeartbeatGrain>(key);
    }

    public static string CleanString(string input)
    {
        var builder = new System.Text.StringBuilder();
        foreach (char c in input)
        {
            if (char.IsLetterOrDigit(c) || c == '-' || c == ':' || c == '.')
            {
                builder.Append(c);
            }
            else
            {
                builder.Append(":");
            }
        }
        return builder.ToString();
    }

    private static long GetPartitionGrainKey(string hubIdentity, int partitionId, bool alreadyCleaned)
    {
        var normalized = alreadyCleaned ? hubIdentity : CleanString(hubIdentity);
        var hubBytes = Encoding.UTF8.GetBytes(normalized);
        var hash = XxHash64.HashToUInt64(hubBytes);
        var composite = (hash << 16) ^ (uint)partitionId;
        return unchecked((long)composite);
    }

    // public static IAsyncStream<TMessage> GetStream<THub, TMessage>(IClusterClient clusterClient,
    //     string streamProviderName, string streamName)
    // {
    //     var streamProvider = clusterClient.GetStreamProvider(streamProviderName);
    //     return GetStream<TMessage>(typeof(THub).FullName!, streamProvider, streamName);
    // }
    //
    // public static IAsyncStream<TMessage> GetStream<TMessage>(string hub, IStreamProvider streamProvider, string streamName)
    // {
    //     var streamId = StreamId.Create(ConnectionNamespace<TMessage>(hub), streamName);
    //     return streamProvider.GetStream<TMessage>(streamId);
    // }
}
