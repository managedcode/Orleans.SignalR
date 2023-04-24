using System;
using ManagedCode.Orleans.SignalR.Core.Config;
using ManagedCode.Orleans.SignalR.Core.SignalR;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;

namespace ManagedCode.Orleans.SignalR.Server.Extensions;

/// <summary>
///     Extension methods for configuring Redis-based scale-out for a SignalR Server in an
///     <see cref="ISignalRServerBuilder" />.
/// </summary>
public static class OrleansDependencyInjectionExtensions
{
    public static ISignalRServerBuilder AddOrleans(this ISignalRServerBuilder signalrBuilder)
    {
        signalrBuilder.Services.AddSingleton(typeof(HubLifetimeManager<>), typeof(OrleansHubLifetimeManager<>));
        return signalrBuilder;
    }
    
    public static ISignalRServerBuilder AddOrleans(this ISignalRServerBuilder signalrBuilder, Action<OrleansSignalROptions> options)
    {
        signalrBuilder.Services.AddOptions<OrleansSignalROptions>().Configure(options);
        signalrBuilder.Services.AddSingleton(typeof(HubLifetimeManager<>), typeof(OrleansHubLifetimeManager<>));
        return signalrBuilder;
    }
}