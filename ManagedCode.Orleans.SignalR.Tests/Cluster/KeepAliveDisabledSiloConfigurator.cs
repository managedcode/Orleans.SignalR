using ManagedCode.Orleans.SignalR.Core.Config;
using Microsoft.Extensions.DependencyInjection;
using Orleans.TestingHost;

namespace ManagedCode.Orleans.SignalR.Tests.Cluster;

public sealed class KeepAliveDisabledSiloConfigurator : ISiloConfigurator
{
    private static readonly TimeSpan _disabledClientTimeout = TimeSpan.FromSeconds(2);

    public void Configure(ISiloBuilder siloBuilder)
    {
        siloBuilder.Services.PostConfigure<OrleansSignalROptions>(options =>
        {
            options.ClientTimeoutInterval = _disabledClientTimeout;
            options.KeepEachConnectionAlive = false;
        });
    }
}

