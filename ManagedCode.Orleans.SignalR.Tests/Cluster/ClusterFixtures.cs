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

public sealed class KeepAliveClusterFixture : ClusterFixtureBase
{
    public KeepAliveClusterFixture()
        : base(builder =>
        {
            builder.Options.InitialSilosCount = 2;
        })
    {
    }
}

[CollectionDefinition(nameof(KeepAliveCluster))]
public sealed class KeepAliveCluster : ICollectionFixture<KeepAliveClusterFixture>
{
}

public sealed class KeepAliveDisabledClusterFixture : ClusterFixtureBase
{
    public KeepAliveDisabledClusterFixture()
        : base(builder =>
        {
            builder.Options.InitialSilosCount = 2;
        })
    {
    }
}

[CollectionDefinition(nameof(KeepAliveDisabledCluster))]
public sealed class KeepAliveDisabledCluster : ICollectionFixture<KeepAliveDisabledClusterFixture>
{
}

public sealed class LongIdleClientClusterFixture : ClusterFixtureBase
{
    public LongIdleClientClusterFixture()
        : base(builder =>
        {
            builder.Options.InitialSilosCount = 2;
            builder.AddSiloBuilderConfigurator<LongIdleSiloConfigurator>();
        })
    {
    }
}

[CollectionDefinition(nameof(LongIdleClientCluster))]
public sealed class LongIdleClientCluster : ICollectionFixture<LongIdleClientClusterFixture>
{
}

public sealed class LongIdleServerClusterFixture : ClusterFixtureBase
{
    public LongIdleServerClusterFixture()
        : base(builder =>
        {
            builder.Options.InitialSilosCount = 2;
            builder.AddSiloBuilderConfigurator<LongIdleSiloConfigurator>();
        })
    {
    }
}

[CollectionDefinition(nameof(LongIdleServerCluster))]
public sealed class LongIdleServerCluster : ICollectionFixture<LongIdleServerClusterFixture>
{
}

public class LoadClusterFixture : ClusterFixtureBase
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

public sealed class LoadClusterDeviceFixture : LoadClusterFixture
{
}

[CollectionDefinition(nameof(LoadClusterDevice))]
public sealed class LoadClusterDevice : ICollectionFixture<LoadClusterDeviceFixture>
{
}

public sealed class LoadClusterBroadcastFixture : LoadClusterFixture
{
}

[CollectionDefinition(nameof(LoadClusterBroadcast))]
public sealed class LoadClusterBroadcast : ICollectionFixture<LoadClusterBroadcastFixture>
{
}

public sealed class LoadClusterGroupFixture : LoadClusterFixture
{
}

[CollectionDefinition(nameof(LoadClusterGroup))]
public sealed class LoadClusterGroup : ICollectionFixture<LoadClusterGroupFixture>
{
}

public sealed class LoadClusterStreamingFixture : LoadClusterFixture
{
}

[CollectionDefinition(nameof(LoadClusterStreaming))]
public sealed class LoadClusterStreaming : ICollectionFixture<LoadClusterStreamingFixture>
{
}

public sealed class LoadClusterInvocationFixture : LoadClusterFixture
{
}

[CollectionDefinition(nameof(LoadClusterInvocation))]
public sealed class LoadClusterInvocation : ICollectionFixture<LoadClusterInvocationFixture>
{
}

public sealed class LoadClusterCascadeFixture : LoadClusterFixture
{
}

[CollectionDefinition(nameof(LoadClusterCascade))]
public sealed class LoadClusterCascade : ICollectionFixture<LoadClusterCascadeFixture>
{
}

public sealed class LoadClusterActivationFixture : LoadClusterFixture
{
}

[CollectionDefinition(nameof(LoadClusterActivation))]
public sealed class LoadClusterActivation : ICollectionFixture<LoadClusterActivationFixture>
{
}
