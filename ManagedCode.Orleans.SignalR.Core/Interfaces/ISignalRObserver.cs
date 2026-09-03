using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR.Protocol;
using Orleans;
using Orleans.Concurrency;
using Orleans.Runtime;

namespace ManagedCode.Orleans.SignalR.Core.Interfaces;

public interface ISignalRObserver : IGrainObserver
{
    [OneWay]
    Task OnNextAsync(HubMessage message);

    [OneWay]
    Task OnNextWithDeliverySourceAsync(HubMessage message, GrainId sourceGrainId);

    [OneWay]
    Task OnNextWithAcknowledgementAsync(
        HubMessage message,
        Guid deliveryId,
        string userGrainKey,
        GrainId sourceGrainId);
}
