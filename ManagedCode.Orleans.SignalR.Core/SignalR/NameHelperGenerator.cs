using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using ManagedCode.Orleans.SignalR.Core.Helpers;
using ManagedCode.Orleans.SignalR.Core.Interfaces;
using Orleans;

namespace ManagedCode.Orleans.SignalR.Core.SignalR;

public static class NameHelperGenerator
{
    // Cache cleaned type names to avoid repeated allocations
    private static readonly ConcurrentDictionary<Type, string> _typeNameCache = new();

    // SearchValues for allowed characters (optimized for .NET 8+)
    private static readonly SearchValues<char> _allowedChars =
        SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-:.");

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ISignalRConnectionHolderGrain GetConnectionHolderGrain<THub>(IGrainFactory grainFactory)
    {
        return grainFactory.GetGrain<ISignalRConnectionHolderGrain>(GetCleanedTypeName<THub>());
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ISignalRConnectionHolderGrain GetConnectionHolderGrain(IGrainFactory grainFactory, string hubKey)
    {
        return grainFactory.GetGrain<ISignalRConnectionHolderGrain>(CleanString(hubKey));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ISignalRConnectionCoordinatorGrain GetConnectionCoordinatorGrain<THub>(IGrainFactory grainFactory)
    {
        return grainFactory.GetGrain<ISignalRConnectionCoordinatorGrain>(GetCleanedTypeName<THub>());
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ISignalRConnectionPartitionGrain GetConnectionPartitionGrain<THub>(IGrainFactory grainFactory, int partitionId)
    {
        var key = GetPartitionGrainKey<THub>(partitionId);
        return grainFactory.GetGrain<ISignalRConnectionPartitionGrain>(key);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ISignalRConnectionPartitionGrain GetConnectionPartitionGrain(IGrainFactory grainFactory, string hubKey, int partitionId)
    {
        var key = GetPartitionGrainKey(hubKey, partitionId, alreadyCleaned: true);
        return grainFactory.GetGrain<ISignalRConnectionPartitionGrain>(key);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ISignalRInvocationGrain GetInvocationGrain<THub>(IGrainFactory grainFactory, string? invocationId)
    {
        var typeName = GetCleanedTypeName<THub>();
        var key = string.Concat(typeName, "::", invocationId ?? "unknown");
        return grainFactory.GetGrain<ISignalRInvocationGrain>(key);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ISignalRUserGrain GetSignalRUserGrain<THub>(IGrainFactory grainFactory, string userId)
    {
        var typeName = GetCleanedTypeName<THub>();
        var cleanUserId = CleanString(userId);
        return grainFactory.GetGrain<ISignalRUserGrain>(string.Concat(typeName, "::", cleanUserId));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ISignalRGroupGrain GetSignalRGroupGrain<THub>(IGrainFactory grainFactory, string groupId)
    {
        var typeName = GetCleanedTypeName<THub>();
        var cleanGroupId = CleanString(groupId);
        return grainFactory.GetGrain<ISignalRGroupGrain>(string.Concat(typeName, "::", cleanGroupId));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ISignalRGroupCoordinatorGrain GetGroupCoordinatorGrain<THub>(IGrainFactory grainFactory)
    {
        return grainFactory.GetGrain<ISignalRGroupCoordinatorGrain>(GetCleanedTypeName<THub>());
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ISignalRGroupCoordinatorGrain GetGroupCoordinatorGrain(IGrainFactory grainFactory, string hubKey)
    {
        return grainFactory.GetGrain<ISignalRGroupCoordinatorGrain>(CleanString(hubKey));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ISignalRGroupPartitionGrain GetGroupPartitionGrain<THub>(IGrainFactory grainFactory, int partitionId)
    {
        var key = GetPartitionGrainKey<THub>(partitionId);
        return grainFactory.GetGrain<ISignalRGroupPartitionGrain>(key);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ISignalRGroupPartitionGrain GetGroupPartitionGrain(IGrainFactory grainFactory, string hubKey, int partitionId)
    {
        var key = GetPartitionGrainKey(hubKey, partitionId, alreadyCleaned: true);
        return grainFactory.GetGrain<ISignalRGroupPartitionGrain>(key);
    }

    public static ISignalRConnectionHeartbeatGrain GetConnectionHeartbeatGrain(IGrainFactory grainFactory, string hubKey, string connectionId)
    {
        var cleanedHub = CleanString(hubKey);
        var cleanedConnection = CleanString(connectionId);
        return grainFactory.GetGrain<ISignalRConnectionHeartbeatGrain>(string.Concat(cleanedHub, "::", cleanedConnection));
    }

    /// <summary>
    /// Gets the cached cleaned type name for a hub type.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string GetCleanedTypeName<THub>()
    {
        return _typeNameCache.GetOrAdd(typeof(THub), static t => CleanString(t.FullName!));
    }

    /// <summary>
    /// Cleans a string by replacing invalid characters with ':'.
    /// Uses SearchValues for optimized character lookup and string.Create for allocation-efficient string building.
    /// </summary>
    public static string CleanString(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input;
        }

        // Fast path: check if any characters need replacement
        var inputSpan = input.AsSpan();
        var firstInvalidIndex = inputSpan.IndexOfAnyExcept(_allowedChars);

        if (firstInvalidIndex < 0)
        {
            // All characters are valid, return original string
            return input;
        }

        // Need to clean - use string.Create for efficient allocation
        return string.Create(input.Length, input, static (span, src) =>
        {
            for (var i = 0; i < src.Length; i++)
            {
                var c = src[i];
                span[i] = _allowedChars.Contains(c) ? c : ':';
            }
        });
    }

    /// <summary>
    /// Gets partition grain key using cached type name.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long GetPartitionGrainKey<THub>(int partitionId)
    {
        var cleanedName = GetCleanedTypeName<THub>();
        return ComputePartitionKey(cleanedName.AsSpan(), partitionId);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long GetPartitionGrainKey(string hubIdentity, int partitionId, bool alreadyCleaned)
    {
        var normalized = alreadyCleaned ? hubIdentity : CleanString(hubIdentity);
        return ComputePartitionKey(normalized.AsSpan(), partitionId);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long ComputePartitionKey(ReadOnlySpan<char> hubIdentity, int partitionId)
    {
        var hash = (ulong)PartitionHelper.ComputeHash(hubIdentity);
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
