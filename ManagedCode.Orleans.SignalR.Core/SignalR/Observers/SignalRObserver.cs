using System;
using System.Collections.Concurrent;
using System.Threading;
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
    private ConcurrentDictionary<GrainId, byte>? _failedSources;

    public void Dispose()
    {
        _onNextAction = null;
        _onDeliveryFailure = null;
        _onDeliveryRecovered = null;
        _onDeliveryAcknowledged = null;
        Interlocked.Exchange(ref _failedSources, null);
    }

    public async Task OnNextAsync(HubMessage message)
    {
        await DeliverAsync(message, null);
    }

    public async Task OnNextWithDeliverySourceAsync(HubMessage message, GrainId sourceGrainId)
    {
        await DeliverAsync(message, sourceGrainId);
    }

    public Task OnNextWithAcknowledgementAsync(
        HubMessage message,
        Guid deliveryId,
        string userGrainKey,
        GrainId sourceGrainId)
    {
        return DeliverAsync(message, sourceGrainId, (deliveryId, userGrainKey));
    }

    private async Task DeliverAsync(
        HubMessage message,
        GrainId? sourceGrainId,
        (Guid DeliveryId, string UserGrainKey)? acknowledgement = null)
    {
        var action = _onNextAction;
        if (action is null)
        {
            return;
        }

        try
        {
            await action.Invoke(message);
        }
        catch (Exception exception)
        {
            if (sourceGrainId is { } source)
            {
                LazyInitializer.EnsureInitialized(ref _failedSources)[source] = 0;
                if (_onDeliveryFailure is { } failed)
                {
                    await failed.Invoke(source, exception);
                }
            }

            throw;
        }

        if (acknowledgement is { } delivery && _onDeliveryAcknowledged is { } acknowledged)
        {
            await acknowledged.Invoke(delivery.DeliveryId, delivery.UserGrainKey);
        }

        var failedSources = Volatile.Read(ref _failedSources);
        if (sourceGrainId is { } recoveredSource &&
            failedSources is not null &&
            failedSources.TryRemove(recoveredSource, out _) &&
            _onDeliveryRecovered is { } recovered)
        {
            try
            {
                await recovered.Invoke(recoveredSource);
            }
            catch
            {
                failedSources[recoveredSource] = 0;
                throw;
            }
        }
    }

    public bool IsExist => _onNextAction != null;
}
