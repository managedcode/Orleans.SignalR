using Orleans.TestingHost;
using Xunit;

namespace ManagedCode.Orleans.SignalR.Tests.Cluster;

[CollectionDefinition(nameof(SiloCluster))]
public class SiloCluster : ICollectionFixture<SiloCluster>, IDisposable
{
    public SiloCluster()
    {
        var builder = new TestClusterBuilder();
        builder.AddSiloBuilderConfigurator<TestSiloConfigurations>();
        builder.AddClientBuilderConfigurator<TestClientConfigurations>();
        Cluster = builder.Build();
        Cluster.Deploy();
    }

    public TestCluster Cluster { get; }

    public void Dispose()
    {
        Cluster.Dispose();
    }
}