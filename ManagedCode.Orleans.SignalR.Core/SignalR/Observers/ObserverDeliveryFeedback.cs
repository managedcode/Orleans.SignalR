using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using ManagedCode.Orleans.SignalR.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Runtime;

namespace ManagedCode.Orleans.SignalR.Core.SignalR.Observers;

internal static class ObserverDeliveryFeedback
{
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Observer feedback must try every registered grain and preserve the original SignalR failure.")]
    public static async Task ReportFailureAsync(
        IGrainFactory grainFactory,
        GrainId sourceGrainId,
        string connectionId,
        Subscription? subscription,
        Exception exception,
        ILogger logger)
    {
        if (subscription is null || !subscription.GetObserver().IsExist)
        {
            return;
        }

        var observerId = subscription.Reference.GetPrimaryKeyString();
        var failureType = exception.GetType().Name;
        var source = grainFactory.GetGrain(sourceGrainId);
        try
        {
            await source.AsReference<IObserverDeliveryFailureReporter>()
                .ReportObserverDeliveryFailure(connectionId, observerId, failureType, exception.Message);
        }
        catch (Exception feedbackException)
        {
            logger.LogDebug(
                feedbackException,
                "Failed to report SignalR delivery failure for connection {ConnectionId} to grain {GrainId}.",
                connectionId,
                sourceGrainId);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Observer recovery feedback is best effort per registered grain.")]
    public static async Task RestoreAsync(
        IGrainFactory grainFactory,
        GrainId sourceGrainId,
        string connectionId,
        Subscription? subscription,
        ILogger logger)
    {
        if (subscription is null || !subscription.GetObserver().IsExist)
        {
            return;
        }

        var source = grainFactory.GetGrain(sourceGrainId);
        try
        {
            await source.AsReference<IObserverConnectionManager>()
                .AddConnection(connectionId, subscription.Reference);
        }
        catch (Exception exception)
        {
            logger.LogDebug(
                exception,
                "Failed to report SignalR delivery recovery for connection {ConnectionId} to grain {GrainId}.",
                connectionId,
                sourceGrainId);
        }
    }
}
