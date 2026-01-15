using ManagedCode.Orleans.SignalR.Core.Config;
using Microsoft.Extensions.DependencyInjection;
using Orleans.TestingHost;

namespace ManagedCode.Orleans.SignalR.Tests.Cluster;

public class UserConfigurationSiloConfigurator : ISiloConfigurator
{
    private static readonly TimeSpan _orleansClientTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan _messageRetention = TimeSpan.FromMinutes(1.1);

    public void Configure(ISiloBuilder siloBuilder)
    {
        siloBuilder.Services.PostConfigure<OrleansSignalROptions>(options =>
        {
            options.ClientTimeoutInterval = _orleansClientTimeout;
            options.KeepEachConnectionAlive = false;
            options.KeepMessageInterval = _messageRetention;
            options.ConnectionPartitionCount = 1;
            options.GroupPartitionCount = 1;
            options.ConnectionsPerPartitionHint = 1_024;
            options.GroupsPerPartitionHint = 64;
        });
    }
}
