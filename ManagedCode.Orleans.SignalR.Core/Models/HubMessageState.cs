using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.SignalR.Protocol;
using Orleans;

namespace ManagedCode.Orleans.SignalR.Core.Models;

[Immutable]
[GenerateSerializer]
public class HubMessageState
{
    [Id(0)]
    public Dictionary<HubMessage, DateTime> Messages { get; set; } = new();
}