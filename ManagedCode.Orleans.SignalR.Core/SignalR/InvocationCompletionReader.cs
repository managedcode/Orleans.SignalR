using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ManagedCode.Communication.CQRS;
using ManagedCode.Orleans.SignalR.Core.Interfaces;
using Microsoft.AspNetCore.SignalR.Protocol;

namespace ManagedCode.Orleans.SignalR.Core.SignalR;

internal static class InvocationCompletionReader
{
    public static async Task<CompletionMessage> ReadTerminalAsync(
        ISignalRInvocationGrain invocationGrain,
        string connectionId,
        string invocationId,
        CancellationToken cancellationToken)
    {
        var result = await invocationGrain.WaitForCompletion(cancellationToken)
            .ToResultAsync(cancellationToken);

        if (result.IsFailed)
        {
            throw new IOException(
                $"Invocation '{invocationId}' failed for connection '{connectionId}': {result.Problem}");
        }

        return result.Value ??
               throw new IOException($"Invocation '{invocationId}' returned no result for connection '{connectionId}'.");
    }
}
