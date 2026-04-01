using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ManagedCode.Orleans.SignalR.Core.SignalR;
using Microsoft.AspNetCore.SignalR;

namespace ManagedCode.Orleans.SignalR.Core.HubContext;

public sealed class OrleansGroupManager<THub>(HubLifetimeManager<THub> lifetimeManager) : IOrleansGroupManager<THub> where THub : Hub
{
    public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
    {
        return lifetimeManager.AddToGroupAsync(connectionId, groupName, cancellationToken);
    }

    public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
    {
        return lifetimeManager.RemoveFromGroupAsync(connectionId, groupName, cancellationToken);
    }

    public Task AddToGroupsAsync(string connectionId, IReadOnlyList<string> groupNames, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(groupNames);

        if (lifetimeManager is OrleansHubLifetimeManager<THub> orleansLifetimeManager)
        {
            return orleansLifetimeManager.AddToGroupsAsync(connectionId, groupNames, cancellationToken);
        }

        return Task.WhenAll(groupNames.Select(groupName => lifetimeManager.AddToGroupAsync(connectionId, groupName, cancellationToken)));
    }

    public Task RemoveFromGroupsAsync(string connectionId, IReadOnlyList<string> groupNames, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(groupNames);

        if (lifetimeManager is OrleansHubLifetimeManager<THub> orleansLifetimeManager)
        {
            return orleansLifetimeManager.RemoveFromGroupsAsync(connectionId, groupNames, cancellationToken);
        }

        return Task.WhenAll(groupNames.Select(groupName => lifetimeManager.RemoveFromGroupAsync(connectionId, groupName, cancellationToken)));
    }
}
