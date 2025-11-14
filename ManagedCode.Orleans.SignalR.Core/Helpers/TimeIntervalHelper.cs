using System;
using ManagedCode.Orleans.SignalR.Core.Config;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using System.Threading;

namespace ManagedCode.Orleans.SignalR.Core.Helpers;

public static class TimeIntervalHelper
{
    public static TimeSpan GetClientTimeoutInterval(IOptions<OrleansSignalROptions> orleansSignalOptions,
        IOptions<HubOptions> hubOptions)
    {
        var timeSpan = orleansSignalOptions.Value.ClientTimeoutInterval;

        if (hubOptions.Value.ClientTimeoutInterval.HasValue && timeSpan > hubOptions.Value.ClientTimeoutInterval)
        {
            timeSpan = hubOptions.Value.ClientTimeoutInterval.Value;
        }

        return timeSpan;
    }

    public static TimeSpan GetClientTimeoutInterval(IOptions<OrleansSignalROptions> orleansSignalOptions,
        IOptions<HubOptions> globalHubOptions, IOptions<HubOptions> hubOptions)
    {
        var timeSpan = orleansSignalOptions.Value.ClientTimeoutInterval;

        if (globalHubOptions.Value.ClientTimeoutInterval.HasValue &&
            timeSpan > globalHubOptions.Value.ClientTimeoutInterval)
        {
            timeSpan = globalHubOptions.Value.ClientTimeoutInterval.Value;
        }

        if (hubOptions.Value.ClientTimeoutInterval.HasValue && timeSpan > hubOptions.Value.ClientTimeoutInterval)
        {
            timeSpan = hubOptions.Value.ClientTimeoutInterval.Value;
        }

        return timeSpan;
    }

    public static TimeSpan GetClientTimeoutInterval(IOptions<HubOptions> globalHubOptions,
        IOptions<HubOptions> hubOptions)
    {
        var timeSpan = TimeSpan.FromSeconds(15);

        if (globalHubOptions.Value.ClientTimeoutInterval.HasValue &&
            timeSpan > globalHubOptions.Value.ClientTimeoutInterval)
        {
            timeSpan = globalHubOptions.Value.ClientTimeoutInterval.Value;
        }

        if (hubOptions.Value.ClientTimeoutInterval.HasValue && timeSpan > hubOptions.Value.ClientTimeoutInterval)
        {
            timeSpan = hubOptions.Value.ClientTimeoutInterval.Value;
        }

        return timeSpan;
    }

    public static TimeSpan GetKeepAliveInterval(IOptions<HubOptions> globalHubOptions, IOptions<HubOptions> hubOptions)
    {
        var timeSpan = TimeSpan.FromSeconds(15);

        if (globalHubOptions.Value.KeepAliveInterval.HasValue)
        {
            timeSpan = globalHubOptions.Value.KeepAliveInterval.Value;
        }

        if (hubOptions.Value.KeepAliveInterval.HasValue)
        {
            timeSpan = hubOptions.Value.KeepAliveInterval.Value;
        }

        return timeSpan;
    }

    public static TimeSpan AddExpirationIntervalBuffer(TimeSpan timeSpan)
    {
        return timeSpan * 1.2;
    }

    public static TimeSpan GetObserverExpiration(IOptions<OrleansSignalROptions> orleansSignalOptions, TimeSpan baseInterval)
    {
        if (!orleansSignalOptions.Value.KeepEachConnectionAlive)
        {
            return Timeout.InfiniteTimeSpan;
        }

        return AddExpirationIntervalBuffer(baseInterval);
    }
}
