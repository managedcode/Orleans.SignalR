using ManagedCode.Orleans.SignalR.Core.Config;
using Microsoft.Extensions.Configuration;
using Orleans.TestingHost;

namespace ManagedCode.Orleans.SignalR.Tests.Cluster;

public class TestClientConfigurations : IClientBuilderConfigurator
{
    public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
    {
        clientBuilder.AddMemoryStreams(new OrleansSignalROptions().StreamProvider);
    }
}