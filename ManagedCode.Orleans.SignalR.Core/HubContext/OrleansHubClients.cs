using System.Collections.Generic;
using Microsoft.AspNetCore.SignalR;

namespace ManagedCode.Orleans.SignalR.Core.HubContext;

internal sealed class OrleansHubClients<T> : IHubClients<T>
{
    private readonly IHubClients _hubClients;

    public OrleansHubClients(IHubClients hubClients)
    {
        _hubClients = hubClients;
    }

    public T Client(string connectionId)
    {
        return TypedClientBuilder<T>.Build(_hubClients.Client(connectionId));
    }

    public T All => TypedClientBuilder<T>.Build(_hubClients.All);

    public T AllExcept(IReadOnlyList<string> excludedConnectionIds)
    {
        return TypedClientBuilder<T>.Build(_hubClients.AllExcept(excludedConnectionIds));
    }

    public T Group(string groupName)
    {
        return TypedClientBuilder<T>.Build(_hubClients.Group(groupName));
    }

    public T GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds)
    {
        return TypedClientBuilder<T>.Build(_hubClients.GroupExcept(groupName, excludedConnectionIds));
    }

    public T Clients(IReadOnlyList<string> connectionIds)
    {
        return TypedClientBuilder<T>.Build(_hubClients.Clients(connectionIds));
    }

    public T Groups(IReadOnlyList<string> groupNames)
    {
        return TypedClientBuilder<T>.Build(_hubClients.Groups(groupNames));
    }

    public T User(string userId)
    {
        return TypedClientBuilder<T>.Build(_hubClients.User(userId));
    }

    public T Users(IReadOnlyList<string> userIds)
    {
        return TypedClientBuilder<T>.Build(_hubClients.Users(userIds));
    }
}