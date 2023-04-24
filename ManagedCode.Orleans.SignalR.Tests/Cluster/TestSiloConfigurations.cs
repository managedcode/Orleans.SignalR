using ManagedCode.Orleans.SignalR.Server.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Orleans.TestingHost;

namespace ManagedCode.Orleans.SignalR.Tests.Cluster;

public class TestSiloConfigurations : ISiloConfigurator
{
    public void Configure(ISiloBuilder siloBuilder)
    {
        //AddOrleansSignalr

        siloBuilder.AddMemoryStreams("SimpleStreamProvider")
            .AddMemoryGrainStorage("PubSubStore");
        
        siloBuilder.Services.AddSignalR().AddOrleans(options =>
        {
            options.StreamProvider = "SimpleStreamProvider";
        });
    }
}