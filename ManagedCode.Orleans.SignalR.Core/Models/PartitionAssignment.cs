using Orleans;

namespace ManagedCode.Orleans.SignalR.Core.Models;

/// <summary>
/// Represents a partition assignment with epoch tracking for consistency during scaling.
/// </summary>
[GenerateSerializer]
[Immutable]
public readonly record struct PartitionAssignment(
    [property: Id(0)] int PartitionId,
    [property: Id(1)] int Epoch)
{
    /// <summary>
    /// Creates an assignment for the current epoch.
    /// </summary>
    public static PartitionAssignment Create(int partitionId, int epoch) => new(partitionId, epoch);
}
