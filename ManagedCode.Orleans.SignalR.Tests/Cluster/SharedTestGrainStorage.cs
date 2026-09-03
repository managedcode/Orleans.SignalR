using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ManagedCode.Orleans.SignalR.Core.Config;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime.Hosting;
using Orleans.Serialization;
using Orleans.Storage;
using Orleans.TestingHost;

namespace ManagedCode.Orleans.SignalR.Tests.Cluster;

internal sealed class SharedStorageSiloConfigurator : ISiloConfigurator
{
    public void Configure(ISiloBuilder siloBuilder)
    {
        siloBuilder.Services.AddGrainStorage(
            OrleansSignalROptions.OrleansSignalRStorage,
            static (services, _) => new SharedTestGrainStorage(
                services.GetRequiredService<Serializer>(),
                services.GetRequiredService<IOptions<ClusterOptions>>()));
    }
}

internal sealed class FailFirstHeartbeatWriteSiloConfigurator : ISiloConfigurator
{
    public void Configure(ISiloBuilder siloBuilder)
    {
        siloBuilder.Services.AddGrainStorage(
            OrleansSignalROptions.OrleansSignalRStorage,
            static (services, _) => new SharedTestGrainStorage(
                services.GetRequiredService<Serializer>(),
                services.GetRequiredService<IOptions<ClusterOptions>>(),
                failFirstHeartbeatWrite: true));
    }
}

/// <summary>
/// Test-only serialized storage shared by every silo process in the same TestingHost cluster.
/// Orleans' built-in memory provider is silo-local and therefore cannot prove relocation durability.
/// </summary>
internal sealed class SharedTestGrainStorage : IGrainStorage
{
    private const string HeartbeatStateName = "SignalRConnectionHeartbeatGrain";
    private static readonly string _storageRoot = Path.Combine(Path.GetTempPath(), "Orleans.SignalR.Tests");
    private readonly string _directory;
    private readonly bool _failFirstHeartbeatWrite;
    private readonly Serializer _serializer;

    public SharedTestGrainStorage(
        Serializer serializer,
        IOptions<ClusterOptions> clusterOptions,
        bool failFirstHeartbeatWrite = false)
    {
        _serializer = serializer;
        _failFirstHeartbeatWrite = failFirstHeartbeatWrite;
        var clusterIdentity = $"{clusterOptions.Value.ServiceId}:{clusterOptions.Value.ClusterId}";
        _directory = Path.Combine(_storageRoot, Hash(clusterIdentity));
        Directory.CreateDirectory(_directory);
    }

    public async Task ReadStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        var path = GetPath(stateName, grainId);
        if (File.Exists(path))
        {
            var data = await File.ReadAllBytesAsync(path);
            grainState.State = _serializer.Deserialize<T>(data);
            grainState.ETag = File.GetLastWriteTimeUtc(path).Ticks.ToString(CultureInfo.InvariantCulture);
            grainState.RecordExists = true;
            return;
        }

        grainState.State = Activator.CreateInstance<T>();
        grainState.ETag = null;
        grainState.RecordExists = false;
    }

    public async Task WriteStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        var path = GetPath(stateName, grainId);
        if (_failFirstHeartbeatWrite && stateName == HeartbeatStateName && TryCreateFailureMarker(path))
        {
            throw new InvalidOperationException("Injected first heartbeat storage write failure.");
        }

        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllBytesAsync(temporaryPath, _serializer.SerializeToArray(grainState.State));
        File.Move(temporaryPath, path, overwrite: true);
        File.WriteAllText($"{path}.success", string.Empty);
        grainState.ETag = File.GetLastWriteTimeUtc(path).Ticks.ToString(CultureInfo.InvariantCulture);
        grainState.RecordExists = true;
    }

    public Task ClearStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        File.Delete(GetPath(stateName, grainId));
        grainState.ETag = null;
        grainState.RecordExists = false;
        return Task.CompletedTask;
    }

    private string GetPath(string stateName, GrainId grainId) =>
        Path.Combine(_directory, $"{Hash($"{stateName}:{grainId}")}.bin");

    internal static bool HasSuccessfulWrite(string stateName, GrainId grainId)
    {
        var fileName = $"{Hash($"{stateName}:{grainId}")}.bin.success";
        return Directory.Exists(_storageRoot) &&
               Directory.EnumerateFiles(_storageRoot, fileName, SearchOption.AllDirectories).Any();
    }

    internal static void ClearWriteEvidence(string stateName, GrainId grainId)
    {
        if (!Directory.Exists(_storageRoot))
        {
            return;
        }

        var stateHash = Hash($"{stateName}:{grainId}");
        foreach (var suffix in new[] { ".bin", ".bin.failure", ".bin.success" })
        {
            foreach (var path in Directory.EnumerateFiles(_storageRoot, $"{stateHash}{suffix}", SearchOption.AllDirectories))
            {
                File.Delete(path);
            }
        }
    }

    private static bool TryCreateFailureMarker(string path)
    {
        try
        {
            using var marker = new FileStream($"{path}.failure", FileMode.CreateNew, FileAccess.Write, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
