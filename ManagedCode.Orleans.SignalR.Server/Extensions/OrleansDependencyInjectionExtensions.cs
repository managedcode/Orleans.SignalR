using System;
using ManagedCode.Orleans.SignalR.Core.Config;
using ManagedCode.Orleans.SignalR.Core.SignalR;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;

namespace ManagedCode.Orleans.SignalR.Server.Extensions;

/// <summary>
///     Extension methods for configuring Orleans-based scale-out for a SignalR Server in an
///     <see cref="ISignalRServerBuilder" />.
/// </summary>
public static class OrleansDependencyInjectionExtensions
{
    public static ISignalRServerBuilder AddOrleans(this ISignalRServerBuilder signalrBuilder)
    {
        return AddOrleans(signalrBuilder, o => { });
    }
    
    public static ISignalRServerBuilder AddOrleans(this ISignalRServerBuilder signalrBuilder, Action<OrleansSignalROptions> options)
    {
        signalrBuilder.Services.AddOptions<OrleansSignalROptions>().Configure(options);
        signalrBuilder.Services.AddSingleton(typeof(HubLifetimeManager<>), typeof(OrleansHubLifetimeManager<>));
        return signalrBuilder;
    }
}