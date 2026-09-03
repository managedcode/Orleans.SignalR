using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using ManagedCode.Orleans.SignalR.Core.Interfaces;
using Microsoft.AspNetCore.SignalR.Protocol;
using Orleans.Runtime;

namespace ManagedCode.Orleans.SignalR.Core.SignalR.Observers;

public class SignalRObserver(
    Func<HubMessage, Task> onNextAction,
    Func<GrainId, Exception, Task>? onDeliveryFailure = null,
    Func<GrainId, Task>? onDeliveryRecovered = null,
    Func<Guid, string, Task>? onDeliveryAcknowledged = null) : ISignalRObserver, IDisposable
{
    private Func<HubMessage, Task>? _onNextAction = onNextAction;
    private Func<GrainId, Exception, Task>? _onDeliveryFailure = onDeliveryFailure;
    private Func<GrainId, Task>? _onDeliveryRecovered = onDeliveryRecovered;
    private Func<Guid, string, Task>? _onDeliveryAcknowledged = onDeliveryAcknowledged;
    private readonly ConcurrentDictionary<GrainId, byte> _failedSources = new();

    public void Dispose()
    {
        _onNextAction = null;
        _onDeliveryFailure = null;
        _onDeliveryRecovered = null;
        _onDeliveryAcknowledged = null;
        _failedSources.Clear();
    }

    public async Task OnNextAsync(HubMessage message)
    {
        await DeliverAsync(message, null);
    }

    public async Task OnNextWithDeliverySourceAsync(HubMessage message, GrainId sourceGrainId)
    {
        await DeliverAsync(message, sourceGrainId);
    }

    public async Task OnNextWithAcknowledgementAsync(
        HubMessage message,
        Guid deliveryId,
        string userGrainKey,
        GrainId sourceGrainId)
    {
        await DeliverAsync(message, sourceGrainId);
        if (_onDeliveryAcknowledged is { } acknowledged)
        {
            await acknowledged.Invoke(deliveryId, userGrainKey);
        }
    }

    private async Task DeliverAsync(HubMessage message, GrainId? sourceGrainId)
    {
        var action = _onNextAction;
        if (action is null)
        {
            return;
        }

        try
        {
            await action.Invoke(message);
            if (sourceGrainId is { } source &&
                _failedSources.TryRemove(source, out _) &&
                _onDeliveryRecovered is { } recovered)
            {
                await recovered.Invoke(source);
            }
        }
        catch (Exception exception)
        {
            if (sourceGrainId is { } source)
            {
                _failedSources[source] = 0;
                if (_onDeliveryFailure is { } failed)
                {
                    await failed.Invoke(source, exception);
                }
            }

            throw;
        }
    }

    public bool IsExist => _onNextAction != null;
}
