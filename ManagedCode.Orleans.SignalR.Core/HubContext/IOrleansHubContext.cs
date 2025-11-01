using Microsoft.AspNetCore.SignalR;

namespace ManagedCode.Orleans.SignalR.Core.HubContext;

/// <summary>
///     A context abstraction for a hub.
/// </summary>
public interface IOrleansHubContext<THub, TClient> where THub : Hub
{
    /// <summary>
    ///     Gets a <see cref="IHubClients" /> that can be used to invoke methods on clients connected to the hub.
    /// </summary>
    IHubClients<TClient> Clients { get; }

    /// <summary>
    ///     Gets a <see cref="IGroupManager" /> that can be used to add and remove connections to named groups.
    /// </summary>
    IGroupManager Groups { get; }
}
