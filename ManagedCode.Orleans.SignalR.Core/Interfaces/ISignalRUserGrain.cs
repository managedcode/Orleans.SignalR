using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using ManagedCode.Orleans.SignalR.Core.SignalR;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Protocol;
using Orleans;

namespace ManagedCode.Orleans.SignalR.Core.Interfaces;

public interface ISignalRUserGrain<THub> : IGrainWithStringKey
{
    Task AddConnection(string connectionId);
    Task RemoveConnection(string connectionId);

    Task SendToUser(InvocationMessage message);
}
