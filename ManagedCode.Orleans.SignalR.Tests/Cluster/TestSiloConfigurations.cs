using ManagedCode.Orleans.SignalR.Core.Config;
using ManagedCode.Orleans.SignalR.Server.Extensions;
using ManagedCode.Orleans.SignalR.Tests;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Orleans.TestingHost;

namespace ManagedCode.Orleans.SignalR.Tests.Cluster;

public class TestSiloConfigurations : ISiloConfigurator
{
    public void Configure(ISiloBuilder siloBuilder)
    {
        siloBuilder.ConfigureOrleansSignalR();
        siloBuilder.AddMemoryGrainStorage(OrleansSignalROptions.OrleansSignalRStorage);
        siloBuilder.Services
            .AddSignalR(options =>
            {
                options.ClientTimeoutInterval = TestDefaults.ClientTimeout;
                options.KeepAliveInterval = TestDefaults.KeepAliveInterval;
            })
            .AddOrleans();

        siloBuilder.Services.Configure<OrleansSignalROptions>(options =>
        {
            options.ClientTimeoutInterval = TestDefaults.ClientTimeout;
            options.KeepMessageInterval = TestDefaults.MessageRetention;
            options.ConnectionPartitionCount = TestDefaults.ConnectionPartitions;
            options.GroupPartitionCount = TestDefaults.GroupPartitions;
            options.ConnectionsPerPartitionHint = TestDefaults.ConnectionsPerPartitionHint;
            options.GroupsPerPartitionHint = TestDefaults.GroupsPerPartitionHint;
        });
    }
}
