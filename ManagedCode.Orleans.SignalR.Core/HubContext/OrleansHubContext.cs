using Microsoft.AspNetCore.SignalR;

namespace ManagedCode.Orleans.SignalR.Core.HubContext;

/// <summary>
/// A context abstraction for a hub.
/// </summary>
public class OrleansHubContext<THub, TClient> :  IOrleansHubContext<THub, TClient> where THub : Hub
{
    public OrleansHubContext(IHubContext<THub> context)
    {
        Clients = new OrleansHubClients<TClient>(context.Clients);
        Groups = context.Groups;
    }
    
    /// <summary>
    /// Gets a <see cref="IHubClients"/> that can be used to invoke methods on clients connected to the hub.
    /// </summary>
    public IHubClients<TClient> Clients { get; }

    /// <summary>
    /// Gets a <see cref="IGroupManager"/> that can be used to add and remove connections to named groups.
    /// </summary>
    public IGroupManager Groups { get; }
}