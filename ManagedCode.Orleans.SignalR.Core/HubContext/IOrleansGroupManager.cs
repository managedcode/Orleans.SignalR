using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;

namespace ManagedCode.Orleans.SignalR.Core.HubContext;

public interface IOrleansGroupManager<THub> : IGroupManager where THub : Hub
{
    Task AddToGroupsAsync(string connectionId, IReadOnlyList<string> groupNames, CancellationToken cancellationToken = default);

    Task RemoveFromGroupsAsync(string connectionId, IReadOnlyList<string> groupNames, CancellationToken cancellationToken = default);
}
