using System;
using ManagedCode.Orleans.SignalR.Core.SignalR;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;

namespace ManagedCode.Orleans.SignalR.Client.Extensions;

public static class OrleansDependencyInjectionExtensions
{
    public static ISignalRServerBuilder AddOrleans(this ISignalRServerBuilder signalrBuilder)
    {
        return AddOrleans(signalrBuilder, o =>
        {
            //o.Configuration = ConfigurationOptions.Parse(redisConnectionString);
        });
    }

    public static ISignalRServerBuilder AddOrleans(this ISignalRServerBuilder signalrBuilder, Action<object> configure)
    {
        signalrBuilder.Services.Configure(configure);
        signalrBuilder.Services.AddSingleton(typeof(HubLifetimeManager<>), typeof(OrleansHubLifetimeManager<>));
        return signalrBuilder;
    }
}