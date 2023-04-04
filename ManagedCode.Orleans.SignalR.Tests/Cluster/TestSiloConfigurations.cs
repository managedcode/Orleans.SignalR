using ManagedCode.Orleans.SignalR.Client.Extensions;
using ManagedCode.Orleans.SignalR.Core.Config;
using ManagedCode.Orleans.SignalR.Server.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Orleans.TestingHost;

namespace ManagedCode.Orleans.SignalR.Tests.Cluster;

public class TestSiloConfigurations : ISiloConfigurator
{
    public void Configure(ISiloBuilder siloBuilder)
    {
        //AddOrleansSignalr

        siloBuilder.AddMemoryStreams(new OrleansSignalROptions().StreamProvider)
            .AddMemoryGrainStorage("PubSubStore");

        siloBuilder.AddOrleansSignalr();

        siloBuilder.Services.AddSignalR().AddOrleans();
    }
}