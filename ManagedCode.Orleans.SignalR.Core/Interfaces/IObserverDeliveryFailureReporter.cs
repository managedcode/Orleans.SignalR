using System.Threading.Tasks;
using Orleans;
using Orleans.Concurrency;

namespace ManagedCode.Orleans.SignalR.Core.Interfaces;

public interface IObserverDeliveryFailureReporter : IGrain
{
    [AlwaysInterleave]
    Task ReportObserverDeliveryFailure(
        string connectionId,
        string observerId,
        string failureType,
        string failureMessage);
}
