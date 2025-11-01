using ManagedCode.Orleans.SignalR.Core.Helpers;
using Xunit;
using Xunit.Abstractions;

namespace ManagedCode.Orleans.SignalR.Tests;

public class ConsistentHashingTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    [Fact]
    public void ConsistentHashRing_Should_Distribute_Connections_Evenly()
    {
        // Arrange
        const int partitionCount = 8;
        const int connectionCount = 100000;
        var ring = new ConsistentHashRing(partitionCount);
        var connections = Enumerable.Range(0, connectionCount)
            .Select(i => $"connection_{i}")
            .ToList();

        // Act
        var distribution = ring.GetDistribution(connections);

        // Assert
        var expectedPerPartition = connectionCount / partitionCount;
        var tolerance = expectedPerPartition * 0.15; // 15% tolerance

        _output.WriteLine($"Expected per partition: {expectedPerPartition}");
        foreach (var kvp in distribution)
        {
            _output.WriteLine($"Partition {kvp.Key}: {kvp.Value} connections ({kvp.Value * 100.0 / connectionCount:F2}%)");
            Assert.InRange(kvp.Value, expectedPerPartition - tolerance, expectedPerPartition + tolerance);
        }
    }

    [Fact]
    public void ConsistentHashRing_Should_Return_Same_Partition_For_Same_ConnectionId()
    {
        // Arrange
        const int partitionCount = 8;
        var ring = new ConsistentHashRing(partitionCount);
        var connectionId = "test_connection_123";

        // Act
        var partition1 = ring.GetPartition(connectionId);
        var partition2 = ring.GetPartition(connectionId);
        var partition3 = ring.GetPartition(connectionId);

        // Assert
        Assert.Equal(partition1, partition2);
        Assert.Equal(partition2, partition3);
    }

    [Fact]
    public void PartitionHelper_Should_Calculate_Optimal_Partition_Count()
    {
        // Arrange & Act & Assert
        Assert.Equal(1, PartitionHelper.GetOptimalPartitionCount(5000));     // < 10k => 1 partition
        Assert.Equal(2, PartitionHelper.GetOptimalPartitionCount(15000));    // 15k => 2 partitions
        Assert.Equal(8, PartitionHelper.GetOptimalPartitionCount(80000));    // 80k => 8 partitions
        Assert.Equal(16, PartitionHelper.GetOptimalPartitionCount(150000));  // 150k => 16 partitions
        Assert.Equal(128, PartitionHelper.GetOptimalPartitionCount(1000000)); // 1M => 128 partitions
    }

    [Fact]
    public void PartitionHelper_Should_Calculate_Optimal_Group_Partition_Count()
    {
        Assert.Equal(1, PartitionHelper.GetOptimalGroupPartitionCount(0));
        Assert.Equal(1, PartitionHelper.GetOptimalGroupPartitionCount(1));
        Assert.Equal(1, PartitionHelper.GetOptimalGroupPartitionCount(1000));
        Assert.Equal(2, PartitionHelper.GetOptimalGroupPartitionCount(1001));
        Assert.Equal(4, PartitionHelper.GetOptimalGroupPartitionCount(3500));
        Assert.Equal(8, PartitionHelper.GetOptimalGroupPartitionCount(8000));
    }

    [Fact]
    public void PartitionHelper_Should_Return_Consistent_PartitionId()
    {
        // Arrange
        const int partitionCount = 8;
        var connectionId = "test_connection_456";

        // Act
        var partition1 = PartitionHelper.GetPartitionId(connectionId, partitionCount);
        var partition2 = PartitionHelper.GetPartitionId(connectionId, partitionCount);

        // Assert
        Assert.Equal(partition1, partition2);
        Assert.InRange(partition1, 0, partitionCount - 1);
    }

    [Fact]
    public void ConsistentHashRing_Should_Minimize_Remapping_When_Adding_Partitions()
    {
        // Arrange
        const int initialPartitions = 4;
        const int newPartitions = 5;
        const int connectionCount = 10000;

        var connections = Enumerable.Range(0, connectionCount)
            .Select(i => $"connection_{i}")
            .ToList();

        var ring1 = new ConsistentHashRing(initialPartitions);
        var ring2 = new ConsistentHashRing(newPartitions);

        // Act
        var mapping1 = connections.ToDictionary(c => c, ring1.GetPartition);
        var mapping2 = connections.ToDictionary(c => c, ring2.GetPartition);

        // Count how many connections changed partitions
        var remappedCount = connections.Count(c => mapping1[c] != mapping2[c]);
        var remappedPercentage = remappedCount * 100.0 / connectionCount;

        // Assert
        // With consistent hashing, approximately 1/N connections should be remapped
        var expectedRemappedPercentage = 100.0 / newPartitions;
        _output.WriteLine($"Remapped: {remappedCount}/{connectionCount} ({remappedPercentage:F2}%)");
        _output.WriteLine($"Expected: ~{expectedRemappedPercentage:F2}%");

        // Allow some tolerance
        Assert.InRange(remappedPercentage, expectedRemappedPercentage - 10, expectedRemappedPercentage + 10);
    }
}
