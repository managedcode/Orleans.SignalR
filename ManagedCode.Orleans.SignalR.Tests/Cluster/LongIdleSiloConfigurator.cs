using System.Reflection;
using ManagedCode.Orleans.SignalR.Server;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.TestingHost;

namespace ManagedCode.Orleans.SignalR.Tests.Cluster;

public class LongIdleSiloConfigurator : ISiloConfigurator
{
    private static readonly TimeSpan _idleAge = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan _quantum = TimeSpan.FromSeconds(2);

    public void Configure(ISiloBuilder siloBuilder)
    {
        siloBuilder.Configure<GrainCollectionOptions>(options =>
        {
            options.CollectionAge = _idleAge;
            options.CollectionQuantum = _quantum;

            SetSpecificCollectionAge<SignalRGroupGrain>(options);
            SetSpecificCollectionAge<SignalRGroupPartitionGrain>(options);
            SetSpecificCollectionAge<SignalRUserGrain>(options);
            SetSpecificCollectionAge<SignalRInvocationGrain>(options);
        });
    }

    private static void SetSpecificCollectionAge<TGrain>(GrainCollectionOptions options)
    {
        var attribute = typeof(TGrain).GetCustomAttribute<GrainTypeAttribute>();
        if (attribute is null)
        {
            return;
        }

        var grainType = attribute.GetGrainType(null!, null!).ToString();
        if (!string.IsNullOrEmpty(grainType))
        {
            options.ClassSpecificCollectionAge[grainType] = _idleAge;
        }
    }
}
