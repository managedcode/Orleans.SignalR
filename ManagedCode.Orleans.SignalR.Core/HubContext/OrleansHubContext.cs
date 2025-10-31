using Microsoft.AspNetCore.SignalR;

namespace ManagedCode.Orleans.SignalR.Core.HubContext;

/// <summary>
///     A context abstraction for a hub.
/// </summary>
public class OrleansHubContext<THub, TClient>(IHubContext<THub> context) : IOrleansHubContext<THub, TClient> where THub : Hub
{
    /// <summary>
    ///     Gets a <see cref="IHubClients" /> that can be used to invoke methods on clients connected to the hub.
    /// </summary>
    public IHubClients<TClient> Clients { get; } = new OrleansHubClients<TClient>(context.Clients);

    /// <summary>
    ///     Gets a <see cref="IGroupManager" /> that can be used to add and remove connections to named groups.
    /// </summary>
    public IGroupManager Groups { get; } = context.Groups;
}