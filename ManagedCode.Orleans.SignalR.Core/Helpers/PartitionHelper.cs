using System;
using System.Collections.Generic;
using System.Linq;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace ManagedCode.Orleans.SignalR.Core.Helpers;

public static class PartitionHelper
{
    private const int VirtualNodesPerPartition = 150; // Number of virtual nodes per physical partition
    private static readonly ConcurrentDictionary<RingCacheKey, ConsistentHashRing> RingCache = new();
    
    public static int GetPartitionId(string connectionId, uint partitionCount)
    {
        if (string.IsNullOrEmpty(connectionId))
            throw new ArgumentException("Connection ID cannot be null or empty", nameof(connectionId));

        if (partitionCount <= 0)
            throw new ArgumentException("Partition count must be greater than 0", nameof(partitionCount));

        var ring = RingCache.GetOrAdd(new RingCacheKey((int)partitionCount, VirtualNodesPerPartition),
            key => new ConsistentHashRing(key.PartitionCount, key.VirtualNodes));

        return ring.GetPartition(connectionId);
    }

    public static int GetOptimalPartitionCount(int expectedConnections)
    {
        // Rule of thumb: ~10,000 connections per partition
        const int connectionsPerPartition = 10000;
        var partitions = Math.Max(1, (expectedConnections + connectionsPerPartition - 1) / connectionsPerPartition);
        
        // Round to nearest power of 2 for better hash distribution
        return (int)Math.Pow(2, Math.Ceiling(Math.Log(partitions, 2)));
    }
    
    public static int GetOptimalGroupPartitionCount(int expectedGroups)
    {
        // Rule of thumb: ~1,000 groups per partition
        const int groupsPerPartition = 1000;
        var partitions = Math.Max(1, (expectedGroups + groupsPerPartition - 1) / groupsPerPartition);
        
        // Round to nearest power of 2 for better hash distribution
        return (int)Math.Pow(2, Math.Ceiling(Math.Log(partitions, 2)));
    }

    private readonly record struct RingCacheKey(int PartitionCount, int VirtualNodes);
}

public class ConsistentHashRing
{
    private readonly uint[] _keys;
    private readonly int[] _partitions;
    private readonly int _partitionCount;

    public ConsistentHashRing(int partitionCount, int virtualNodes = 150)
    {
        if (partitionCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(partitionCount), "Partition count must be greater than zero.");
        }

        _partitionCount = partitionCount;

        var ring = InitializeRing(partitionCount, virtualNodes);
        _keys = ring.Keys.ToArray();
        _partitions = ring.Values.ToArray();
    }

    private static SortedList<uint, int> InitializeRing(int partitionCount, int virtualNodes)
    {
        var ring = new SortedList<uint, int>(partitionCount * virtualNodes);

        for (var partition = 0; partition < partitionCount; partition++)
        {
            for (var vnode = 0; vnode < virtualNodes; vnode++)
            {
                var virtualNodeKey = $"partition-{partition}-vnode-{vnode}";
                var hash = GetHash(virtualNodeKey);
                ring[hash] = partition;
            }
        }

        return ring;
    }

    public int GetPartition(string key)
    {
        if (_keys.Length == 0)
            return 0;

        var hash = GetHash(key);

        var index = Array.BinarySearch(_keys, hash);
        if (index < 0)
            index = ~index;

        if (index >= _keys.Length)
            index = 0;

        return _partitions[index];
    }

    private static uint GetHash(string key)
    {
        using var md5 = MD5.Create();
        var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(key));
        return BitConverter.ToUInt32(hash, 0);
    }

    public Dictionary<int, int> GetDistribution(IEnumerable<string> keys)
    {
        var distribution = new Dictionary<int, int>();
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
