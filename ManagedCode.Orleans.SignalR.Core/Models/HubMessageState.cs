using System.Collections.Generic;
using Orleans;

namespace ManagedCode.Orleans.SignalR.Core.Models;

[GenerateSerializer]
public class HubMessageState
{
    [Id(0)]
    public List<QueuedHubMessage> Queue { get; set; } = [];
}
