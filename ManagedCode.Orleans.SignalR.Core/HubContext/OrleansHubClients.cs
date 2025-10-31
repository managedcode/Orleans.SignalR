using System.Collections.Generic;
using Microsoft.AspNetCore.SignalR;

namespace ManagedCode.Orleans.SignalR.Core.HubContext;

internal sealed class OrleansHubClients<T>(IHubClients hubClients) : IHubClients<T>
{
    public T Client(string connectionId)
    {
        return TypedClientBuilder<T>.Build(hubClients.Client(connectionId));
    }

    public T All => TypedClientBuilder<T>.Build(hubClients.All);

    public T AllExcept(IReadOnlyList<string> excludedConnectionIds)
    {
        return TypedClientBuilder<T>.Build(hubClients.AllExcept(excludedConnectionIds));
    }

    public T Group(string groupName)
    {
        return TypedClientBuilder<T>.Build(hubClients.Group(groupName));
    }

    public T GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds)
    {
        return TypedClientBuilder<T>.Build(hubClients.GroupExcept(groupName, excludedConnectionIds));
    }

    public T Clients(IReadOnlyList<string> connectionIds)
    {
        return TypedClientBuilder<T>.Build(hubClients.Clients(connectionIds));
    }

    public T Groups(IReadOnlyList<string> groupNames)
    {
        return TypedClientBuilder<T>.Build(hubClients.Groups(groupNames));
    }

    public T User(string userId)
    {
        return TypedClientBuilder<T>.Build(hubClients.User(userId));
    }

    public T Users(IReadOnlyList<string> userIds)
    {
        return TypedClientBuilder<T>.Build(hubClients.Users(userIds));
    }
}