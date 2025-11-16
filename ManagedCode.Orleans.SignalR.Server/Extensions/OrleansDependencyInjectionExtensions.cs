using System;
using System.Reflection;
using ManagedCode.Orleans.SignalR.Core.Config;
using ManagedCode.Orleans.SignalR.Core.HubContext;
using ManagedCode.Orleans.SignalR.Core.SignalR;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;

namespace ManagedCode.Orleans.SignalR.Server.Extensions;

/// <summary>
///     Extension methods for configuring Orleans-based scale-out for a SignalR Server in an
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
        signalrBuilder.Services.AddSingleton(typeof(IOrleansHubContext<,>), typeof(OrleansHubContext<,>));

        return signalrBuilder;
    }

    public static ISiloBuilder ConfigureOrleansSignalR(this ISiloBuilder siloBuilder)
    {
        var timeSpan = TimeSpan.FromMinutes(7);

        void SetSpecificCollectionAge<T>(GrainCollectionOptions options)
        {
            var grainClassName = typeof(T).FullName ?? typeof(T).Name;
            options.ClassSpecificCollectionAge[grainClassName] = timeSpan;
        }

        return siloBuilder.Configure<GrainCollectionOptions>(options =>
        {
            SetSpecificCollectionAge<SignalRConnectionHolderGrain>(options);
            SetSpecificCollectionAge<SignalRGroupGrain>(options);
            SetSpecificCollectionAge<SignalRInvocationGrain>(options);
            SetSpecificCollectionAge<SignalRUserGrain>(options);
        });
    }

    /// <summary>
    ///     Registers the default in-memory storage provider for Orleans.SignalR grains.
    ///     This wraps <see cref="HostingExtensions.AddMemoryGrainStorage"/> with the storage name required by the built-in grains.
    /// </summary>
    public static ISiloBuilder AddOrleansSignalRInMemoryStorage(this ISiloBuilder siloBuilder)
    {
        ArgumentNullException.ThrowIfNull(siloBuilder);
        return siloBuilder.AddMemoryGrainStorage(OrleansSignalROptions.OrleansSignalRStorage);
    }
}
