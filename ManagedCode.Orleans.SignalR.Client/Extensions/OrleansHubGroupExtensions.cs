using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ManagedCode.Orleans.SignalR.Core.HubContext;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;

namespace ManagedCode.Orleans.SignalR.Client.Extensions;

public static class OrleansHubGroupExtensions
{
    public static Task AddToGroupsAsync<THub>(this THub hub, IReadOnlyList<string> groupNames, CancellationToken cancellationToken = default)
        where THub : Hub
    {
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(groupNames);

        var groupManager = ResolveGroupManager(hub);
        return groupManager.AddToGroupsAsync(hub.Context.ConnectionId, groupNames, cancellationToken);
    }

    public static Task RemoveFromGroupsAsync<THub>(this THub hub, IReadOnlyList<string> groupNames, CancellationToken cancellationToken = default)
        where THub : Hub
    {
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(groupNames);

        var groupManager = ResolveGroupManager(hub);
        return groupManager.RemoveFromGroupsAsync(hub.Context.ConnectionId, groupNames, cancellationToken);
    }

    private static IOrleansGroupManager<THub> ResolveGroupManager<THub>(THub hub) where THub : Hub
    {
        var serviceProvider = (hub.Context.GetHttpContext()?.RequestServices) ?? throw new InvalidOperationException("Unable to resolve SignalR services for the current hub connection.");

        return serviceProvider.GetRequiredService<IOrleansGroupManager<THub>>();
    }
}
