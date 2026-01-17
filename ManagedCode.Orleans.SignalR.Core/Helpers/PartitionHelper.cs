using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO.Hashing;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;

namespace ManagedCode.Orleans.SignalR.Core.Helpers;

public static class PartitionHelper
{
    private const int VirtualNodesPerPartition = 150; // Number of virtual nodes per physical partition
    private const int MaxStackAllocSize = 256; // Max bytes for stackalloc
    private static readonly ConcurrentDictionary<_ringCacheKey, ConsistentHashRing> _ringCache = new();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetPartitionId(string connectionId, uint partitionCount)
    {
        ArgumentException.ThrowIfNullOrEmpty(connectionId);
        ArgumentOutOfRangeException.ThrowIfZero(partitionCount);

        var ring = _ringCache.GetOrAdd(new _ringCacheKey((int)partitionCount, VirtualNodesPerPartition),
            static key => new ConsistentHashRing(key.PartitionCount, key.VirtualNodes));

        return ring.GetPartition(connectionId);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetOptimalPartitionCount(int expectedConnections)
    {
        return GetOptimalPartitionCount(expectedConnections, 10_000);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetOptimalPartitionCount(int expectedConnections, int connectionsPerPartition)
    {
        var perPartition = Math.Max(1, connectionsPerPartition);
        var partitions = Math.Max(1, (expectedConnections + perPartition - 1) / perPartition);
        return (int)BitOperations.RoundUpToPowerOf2((uint)partitions);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetOptimalGroupPartitionCount(int expectedGroups)
    {
        return GetOptimalGroupPartitionCount(expectedGroups, 1_000);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetOptimalGroupPartitionCount(int expectedGroups, int groupsPerPartition)
    {
        var perPartition = Math.Max(1, groupsPerPartition);
        var partitions = Math.Max(1, (expectedGroups + perPartition - 1) / perPartition);
        return (int)BitOperations.RoundUpToPowerOf2((uint)partitions);
    }

    /// <summary>
    /// Computes hash using stack allocation for small strings, ArrayPool for larger ones.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static uint ComputeHash(ReadOnlySpan<char> key)
    {
        var maxByteCount = Encoding.UTF8.GetMaxByteCount(key.Length);

        if (maxByteCount <= MaxStackAllocSize)
        {
            Span<byte> buffer = stackalloc byte[maxByteCount];
            var bytesWritten = Encoding.UTF8.GetBytes(key, buffer);
            return unchecked((uint)XxHash64.HashToUInt64(buffer[..bytesWritten]));
        }

        var rentedBuffer = ArrayPool<byte>.Shared.Rent(maxByteCount);
        try
        {
            var bytesWritten = Encoding.UTF8.GetBytes(key, rentedBuffer);
            return unchecked((uint)XxHash64.HashToUInt64(rentedBuffer.AsSpan(0, bytesWritten)));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rentedBuffer);
        }
    }

    private readonly record struct _ringCacheKey(int PartitionCount, int VirtualNodes);
}

public sealed class ConsistentHashRing
{
    private readonly uint[] _keys;
    private readonly int[] _partitions;
    private readonly int _partitionCount;

    public ConsistentHashRing(int partitionCount, int virtualNodes = 150)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(partitionCount);

        _partitionCount = partitionCount;

        var ring = InitializeRing(partitionCount, virtualNodes);
        _keys = ring.Keys.ToArray();
        _partitions = ring.Values.ToArray();
    }

    private static SortedList<uint, int> InitializeRing(int partitionCount, int virtualNodes)
    {
        var ring = new SortedList<uint, int>(partitionCount * virtualNodes);

        Span<char> keyBuffer = stackalloc char[64]; // "partition-XXXX-vnode-XXXX" max ~25 chars

        for (var partition = 0; partition < partitionCount; partition++)
        {
            for (var vnode = 0; vnode < virtualNodes; vnode++)
            {
                // Build key without allocation using TryFormat
                var written = 0;
                "partition-".AsSpan().CopyTo(keyBuffer);
                written += 10;
                partition.TryFormat(keyBuffer[written..], out var partitionChars, default, CultureInfo.InvariantCulture);
                written += partitionChars;
                "-vnode-".AsSpan().CopyTo(keyBuffer[written..]);
                written += 7;
                vnode.TryFormat(keyBuffer[written..], out var vnodeChars, default, CultureInfo.InvariantCulture);
                written += vnodeChars;

                var hash = PartitionHelper.ComputeHash(keyBuffer[..written]);
                ring[hash] = partition;
            }
        }

        return ring;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetPartition(string key)
    {
        if (_keys.Length == 0)
        {
            return 0;
        }

        var hash = PartitionHelper.ComputeHash(key.AsSpan());

        var index = Array.BinarySearch(_keys, hash);
        if (index < 0)
        {
            index = ~index;
        }

        if (index >= _keys.Length)
        {
            index = 0;
        }

        return _partitions[index];
    }

    public Dictionary<int, int> GetDistribution(IEnumerable<string> keys)
    {
        var distribution = new Dictionary<int, int>(_partitionCount);
        for (var i = 0; i < _partitionCount; i++)
        {
            distribution[i] = 0;
        }

        foreach (var key in keys)
        {
            var partition = GetPartition(key);
            distribution[partition]++;
        }

        return distribution;
    }
}
