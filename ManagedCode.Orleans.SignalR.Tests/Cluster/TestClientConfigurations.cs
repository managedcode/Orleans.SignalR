using ManagedCode.Orleans.SignalR.Client.Extensions;
using ManagedCode.Orleans.SignalR.Core.Config;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans.TestingHost;

namespace ManagedCode.Orleans.SignalR.Tests.Cluster;

public class TestClientConfigurations : IClientBuilderConfigurator
{
    public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
    {
        clientBuilder.AddMemoryStreams(OrleansSignalROptions.DefaultSignalRStreamProvider);
        
        clientBuilder.Services
            .AddSignalR()
            .AddOrleans(options =>
            {
                options.StreamProvider = OrleansSignalROptions.DefaultSignalRStreamProvider;
            });
    }
}