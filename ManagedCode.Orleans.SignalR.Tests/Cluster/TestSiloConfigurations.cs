using ManagedCode.Orleans.SignalR.Core.Config;
using ManagedCode.Orleans.SignalR.Server.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Orleans.TestingHost;

namespace ManagedCode.Orleans.SignalR.Tests.Cluster;

public class TestSiloConfigurations : ISiloConfigurator
{
    public void Configure(ISiloBuilder siloBuilder)
    {
        siloBuilder
            .AddMemoryGrainStorage("PubSubStore")
            .AddMemoryStreams(OrleansSignalROptions.DefaultSignalRStreamProvider)
            .AddMemoryGrainStorage(OrleansSignalROptions.OrleansSignalRStorage);
        
        siloBuilder.Services
            .AddSignalR()
            .AddOrleans(options =>
            {
                options.StreamProvider = OrleansSignalROptions.DefaultSignalRStreamProvider;
            });
    }
}