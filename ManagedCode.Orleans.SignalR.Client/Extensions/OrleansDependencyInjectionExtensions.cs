using System;
using ManagedCode.Orleans.SignalR.Core.Config;
using ManagedCode.Orleans.SignalR.Core.SignalR;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;

namespace ManagedCode.Orleans.SignalR.Client.Extensions;

public static class OrleansDependencyInjectionExtensions
{
    public static ISignalRServerBuilder AddOrleans(this ISignalRServerBuilder signalrBuilder)
    {
        return signalrBuilder.AddOrleans(o =>
        {
            //skip
        });
    }
    
    public static ISignalRServerBuilder AddOrleans(this ISignalRServerBuilder signalrBuilder, Action<OrleansSignalROptions> options)
    {
        signalrBuilder.Services.AddOptions<OrleansSignalROptions>().Configure(options);
        signalrBuilder.Services.AddSingleton(typeof(HubLifetimeManager<>), typeof(OrleansHubLifetimeManager<>));
        return signalrBuilder;
    }
}