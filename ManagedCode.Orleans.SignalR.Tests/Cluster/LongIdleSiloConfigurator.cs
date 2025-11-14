using System;
using System.Reflection;
using ManagedCode.Orleans.SignalR.Server;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.TestingHost;

namespace ManagedCode.Orleans.SignalR.Tests.Cluster;

public class LongIdleSiloConfigurator : ISiloConfigurator
{
    private static readonly TimeSpan IdleAge = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan Quantum = TimeSpan.FromSeconds(2);

    public void Configure(ISiloBuilder siloBuilder)
    {
        siloBuilder.Configure<GrainCollectionOptions>(options =>
        {
            options.CollectionAge = IdleAge;
            options.CollectionQuantum = Quantum;

            SetSpecificCollectionAge<SignalRConnectionHolderGrain>(options);
            SetSpecificCollectionAge<SignalRConnectionPartitionGrain>(options);
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
            options.ClassSpecificCollectionAge[grainType] = IdleAge;
        }
    }
}
