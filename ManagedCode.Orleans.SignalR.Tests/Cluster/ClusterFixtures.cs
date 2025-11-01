using Orleans.TestingHost;
using Xunit;

namespace ManagedCode.Orleans.SignalR.Tests.Cluster;

public abstract class ClusterFixtureBase : IDisposable
{
    protected ClusterFixtureBase(Action<TestClusterBuilder>? configureBuilder = null)
    {
        var builder = new TestClusterBuilder();
        builder.AddSiloBuilderConfigurator<TestSiloConfigurations>();
        builder.AddClientBuilderConfigurator<TestClientConfigurations>();
        configureBuilder?.Invoke(builder);

        Cluster = builder.Build();
        Cluster.Deploy();
    }

    public TestCluster Cluster { get; }

    public void Dispose()
    {
        Cluster.Dispose();
    }
}

public sealed class SmokeClusterFixture : ClusterFixtureBase
{
    public SmokeClusterFixture()
        : base(builder =>
        {
            builder.Options.InitialSilosCount = 2;
        })
    {
    }
}

[CollectionDefinition(nameof(SmokeCluster))]
public sealed class SmokeCluster : ICollectionFixture<SmokeClusterFixture>
{
}

public sealed class LoadClusterFixture : ClusterFixtureBase
{
    public LoadClusterFixture()
        : base(builder =>
        {
            builder.Options.InitialSilosCount = 6;
        })
    {
    }
}

[CollectionDefinition(nameof(LoadCluster))]
public sealed class LoadCluster : ICollectionFixture<LoadClusterFixture>
{
}
