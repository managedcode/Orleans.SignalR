using System;
using ManagedCode.Orleans.SignalR.Core.Config;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.TestingHost;

namespace ManagedCode.Orleans.SignalR.Tests.Cluster;

public class UserConfigurationSiloConfigurator : ISiloConfigurator
{
    private static readonly TimeSpan OrleansClientTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MessageRetention = TimeSpan.FromMinutes(1.1);

    public void Configure(ISiloBuilder siloBuilder)
    {
        siloBuilder.Services.PostConfigure<OrleansSignalROptions>(options =>
        {
            options.ClientTimeoutInterval = OrleansClientTimeout;
            options.KeepEachConnectionAlive = false;
            options.KeepMessageInterval = MessageRetention;
            options.ConnectionPartitionCount = 1;
            options.GroupPartitionCount = 1;
            options.ConnectionsPerPartitionHint = 1_024;
            options.GroupsPerPartitionHint = 64;
        });
    }
}
