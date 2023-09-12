using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ManagedCode.Orleans.SignalR.Core.Config;
using ManagedCode.Orleans.SignalR.Core.Helpers;
using ManagedCode.Orleans.SignalR.Core.Interfaces;
using ManagedCode.Orleans.SignalR.Core.Models;
using ManagedCode.Orleans.SignalR.Core.SignalR.Observers;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans;

namespace ManagedCode.Orleans.SignalR.Core.SignalR;

public class OrleansHubLifetimeManager<THub> : HubLifetimeManager<THub> where THub : Hub
{
    private readonly IClusterClient _clusterClient;
    private readonly HubConnectionStore _connections = new();
    private readonly IOptions<HubOptions> _globalHubOptions;
    private readonly IOptions<HubOptions<THub>> _hubOptions;
    private readonly ILogger _logger;
    private readonly IOptions<OrleansSignalROptions> _orleansSignalOptions;

    public OrleansHubLifetimeManager(ILogger<OrleansHubLifetimeManager<THub>> logger, IClusterClient clusterClient,
        IHubProtocolResolver hubProtocolResolver, IOptions<OrleansSignalROptions> orleansSignalOptions,
        IOptions<HubOptions> globalHubOptions, IOptions<HubOptions<THub>> hubOptions)
    {
        _logger = logger;
        _orleansSignalOptions = orleansSignalOptions;
        _globalHubOptions = globalHubOptions;
        _hubOptions = hubOptions;
        _clusterClient = clusterClient;
    }

    public override async Task OnConnectedAsync(HubConnectionContext connection)
    {
        _connections.Add(connection);
        var subscription = CreateConnectionObserver(connection);

        if (_orleansSignalOptions.Value.KeepEachConnectionAlive)
        {
            var connectionHolderGrain = NameHelperGenerator.GetConnectionHolderGrain<THub>(_clusterClient);
            subscription.AddGrain(connectionHolderGrain);
            await Task.Run(() => connectionHolderGrain.AddConnection(connection.ConnectionId, subscription.Reference));

        }

        if (!string.IsNullOrEmpty(connection.UserIdentifier))
        {
            var userGrain = NameHelperGenerator.GetSignalRUserGrain<THub>(_clusterClient, connection.UserIdentifier!);
            subscription.AddGrain(userGrain);
            await Task.Run(() => userGrain.AddConnection(connection.ConnectionId, subscription.Reference));
            _ = Task.Run(() => userGrain.RequestMessage());
        }
    }

    public override async Task OnDisconnectedAsync(HubConnectionContext connection)
    {
        _connections.Remove(connection);
        
        using var subscription = connection.Features.Get<Subscription>();

        if (subscription is null)
            return;

        var tasks = new List<Task>(subscription.Grains.Count);

        foreach (var grain in subscription.Grains)
            tasks.Add(grain.RemoveConnection(connection.ConnectionId, subscription.Reference));
        
        await Task.Run(() => Task.WhenAll(tasks));
    }

    public override Task SendAllAsync(string methodName, object?[] args, CancellationToken cancellationToken = new())
    {
        var message = new InvocationMessage(methodName, args);
        return Task.Run(() => NameHelperGenerator.GetConnectionHolderGrain<THub>(_clusterClient).SendToAll(message), cancellationToken);
    }

    public override Task SendAllExceptAsync(string methodName, object?[] args,
        IReadOnlyList<string> excludedConnectionIds, CancellationToken cancellationToken = new())
    {
        var message = new InvocationMessage(methodName, args);
        return Task.Run(() => NameHelperGenerator.GetConnectionHolderGrain<THub>(_clusterClient)
            .SendToAllExcept(message, excludedConnectionIds.ToArray()), cancellationToken);
    }

    public override Task SendConnectionAsync(string connectionId, string methodName, object?[] args,
        CancellationToken cancellationToken = new())
    {
        var message = new InvocationMessage(methodName, args);
        return Task.Run(() => NameHelperGenerator.GetConnectionHolderGrain<THub>(_clusterClient)
            .SendToConnection(message, connectionId), cancellationToken);
    }

    public override Task SendConnectionsAsync(IReadOnlyList<string> connectionIds, string methodName, object?[] args,
        CancellationToken cancellationToken = new())
    {
        var message = new InvocationMessage(methodName, args);
        return Task.Run(() => NameHelperGenerator.GetConnectionHolderGrain<THub>(_clusterClient)
            .SendToConnections(message, connectionIds.ToArray()), cancellationToken);
    }

    public override Task SendGroupAsync(string groupName, string methodName, object?[] args,
        CancellationToken cancellationToken = new())
    {
        var message = new InvocationMessage(methodName, args);
        return Task.Run(() => NameHelperGenerator.GetSignalRGroupGrain<THub>(_clusterClient, groupName).SendToGroup(message), cancellationToken);
    }

    public override Task SendGroupsAsync(IReadOnlyList<string> groupNames, string methodName, object?[] args,
        CancellationToken cancellationToken = new())
    {
        var message = new InvocationMessage(methodName, args);
        var tasks = new List<Task>(groupNames.Count);

        foreach (var groupName in groupNames)
            tasks.Add(NameHelperGenerator.GetSignalRGroupGrain<THub>(_clusterClient, groupName).SendToGroup(message));

        return Task.Run(() => Task.WhenAll(tasks), cancellationToken);
    }

    public override Task SendGroupExceptAsync(string groupName, string methodName, object?[] args,
        IReadOnlyList<string> excludedConnectionIds, CancellationToken cancellationToken = new())
    {
        var message = new InvocationMessage(methodName, args);
        return Task.Run(() => NameHelperGenerator.GetSignalRGroupGrain<THub>(_clusterClient, groupName)
            .SendToGroupExcept(message, excludedConnectionIds.ToArray()), cancellationToken);
    }

    public override Task SendUserAsync(string userId, string methodName, object?[] args,
        CancellationToken cancellationToken = new())
    {
        var message = new InvocationMessage(methodName, args);
        return Task.Run(() => NameHelperGenerator.GetSignalRUserGrain<THub>(_clusterClient, userId).SendToUser(message), cancellationToken);
    }

    public override Task SendUsersAsync(IReadOnlyList<string> userIds, string methodName, object?[] args,
        CancellationToken cancellationToken = new())
    {
        var message = new InvocationMessage(methodName, args);
        var tasks = new List<Task>(userIds.Count);
        foreach (var userId in userIds)
            tasks.Add(NameHelperGenerator.GetSignalRUserGrain<THub>(_clusterClient, userId).SendToUser(message));

        return Task.Run(() => Task.WhenAll(tasks), cancellationToken);
    }

    public override async Task AddToGroupAsync(string connectionId, string groupName,
        CancellationToken cancellationToken = new())
    {
        var subscription = GetSubscription(connectionId);
        if (subscription is null)
            return;

        var groupGrain = NameHelperGenerator.GetSignalRGroupGrain<THub>(_clusterClient, groupName);
        await Task.Run(() => groupGrain.AddConnection(connectionId, subscription.Reference), cancellationToken);

        subscription.AddGrain(groupGrain);
    }

    public override async Task RemoveFromGroupAsync(string connectionId, string groupName,
        CancellationToken cancellationToken = new())
    {
        var subscription = GetSubscription(connectionId);
        if (subscription is null)
            return;

        var groupGrain = NameHelperGenerator.GetSignalRGroupGrain<THub>(_clusterClient, groupName);
        await Task.Run(() => groupGrain.RemoveConnection(connectionId, subscription.Reference), cancellationToken);

        subscription.RemoveGrain(groupGrain);
    }

    public override async Task<T> InvokeConnectionAsync<T>(string connectionId, string methodName, object?[] args,
        CancellationToken cancellationToken)
    {
        // send thing
        if (string.IsNullOrEmpty(connectionId))
            throw new ArgumentNullException(nameof(connectionId));

        var connection = _connections[connectionId];
        
        // ID needs to be unique for each invocation and across servers, we generate a GUID every time, that should provide enough uniqueness guarantees.
        var invocationId = GenerateInvocationId();

        var tcs = new TaskCompletionSource<T>();

        using var subscription = CreateSubscription(message =>
        {
            if (message is CompletionMessage completionMessage)
            {
                if (completionMessage.HasResult)
                    tcs.SetResult((T)completionMessage.Result!);
                else
                    tcs.SetException(new Exception(completionMessage.Error));
            }

            return Task.CompletedTask;
        });

        var invocationGrain = NameHelperGenerator.GetInvocationGrain<THub>(_clusterClient, invocationId);
        subscription.AddGrain(invocationGrain);
        await Task.Run(()=> invocationGrain.AddInvocation(subscription.Reference, 
            new InvocationInfo(connectionId, invocationId, typeof(T))), cancellationToken);

        var invocationMessage = new InvocationMessage(invocationId, methodName, args);

        if (connection == null)
        {
            // TODO: Need to handle other server going away while waiting for connection result
            var invocation = await Task.Run(() => NameHelperGenerator.GetConnectionHolderGrain<THub>(_clusterClient)
                .SendToConnection(invocationMessage, connectionId));

            if (invocation == false)
                throw new IOException($"Connection '{connectionId}' does not exist.");
        }
        else
        {

            try
            {
                await Task.Run(() => connection.WriteAsync(invocationMessage, cancellationToken), cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "InvokeConnectionAsync connection {ConnectionConnectionId} failed", connection.ConnectionId);
                throw;
            }
            
        }

        try
        {
            return await Task.Run(() => tcs.Task, cancellationToken);
        }
        catch
        {
            // ConnectionAborted will trigger a generic "Canceled" exception from the task, let's convert it into a more specific message.
            if (connection?.ConnectionAborted.IsCancellationRequested == true)
                throw new IOException($"Connection '{connectionId}' disconnected.");

            throw;
        }
        finally
        {
            if (connection is not null)
            {
                foreach (var grain in subscription.Grains)
                    await Task.Run(() => grain.RemoveConnection(connection.ConnectionId, subscription.Reference), cancellationToken);
            }
        }
    }

    public override async Task SetConnectionResultAsync(string connectionId, CompletionMessage result)
    {
        await Task.Run(() => NameHelperGenerator.GetInvocationGrain<THub>(_clusterClient, result.InvocationId)
            .TryCompleteResult(connectionId, result));
    }

    public override bool TryGetReturnType(string invocationId, [NotNullWhen(true)] out Type? type)
    {
        var returnType = NameHelperGenerator.GetInvocationGrain<THub>(_clusterClient, invocationId).TryGetReturnType();

        var timeSpan =
            TimeIntervalHelper.GetClientTimeoutInterval(_orleansSignalOptions, _globalHubOptions, _hubOptions);
        Task.WaitAny(returnType, Task.Delay(timeSpan * 0.8));

        if (returnType.IsCompleted)
        {
            type = returnType.Result.GetReturnType();
            return returnType.Result.Result;
        }

        type = null;
        return false;
    }

    private Subscription CreateConnectionObserver(HubConnectionContext connection)
    {
        WeakReference<HubConnectionContext> weakConnection = new(connection);
        var subscription = CreateSubscription(message => OnNextAsync(weakConnection, message));
        connection.Features.Set(subscription);
        return subscription;
    }


    private Subscription CreateSubscription(Func<HubMessage, Task> onNextAction)
    {
        var timeSpan = TimeIntervalHelper.GetClientTimeoutInterval(_orleansSignalOptions, _globalHubOptions, _hubOptions);
        var subscription = new Subscription(new SignalRObserver(onNextAction), timeSpan);
        var reference = _clusterClient.CreateObjectReference<ISignalRObserver>(subscription.GetObserver());
        subscription.SetReference(reference);
        return subscription;
    }

    private async Task OnNextAsync(WeakReference<HubConnectionContext> connectionReference, HubMessage message)
    {
        if (!connectionReference.TryGetTarget(out var connection))
        {
            return;
        }
        try
        {
            await Task.Run(() => connection.WriteAsync(message));
        }
        catch (Exception ex)
        {
            if (message is InvocationMessage invocation)
                if (!string.IsNullOrEmpty(invocation.InvocationId))
                    await Task.Run(() => NameHelperGenerator.GetInvocationGrain<THub>(_clusterClient, invocation.InvocationId)
                        .TryCompleteResult(connection.ConnectionId,
                            CompletionMessage.WithError(invocation.InvocationId,
                                $"Connection disconnected. Reason:{ex.Message}")));

            //todo: maybe it's good idea to remove the connection?

            _logger.LogError(ex, "OnNextAsync connection {ConnectionConnectionId} failed", connection.ConnectionId);
        }
    }

    private static string GenerateInvocationId()
    {
        Span<byte> buffer = stackalloc byte[16];
        var success = Guid.NewGuid().TryWriteBytes(buffer);
        Debug.Assert(success);
        // 16 * 4/3 = 21.333 which means base64 encoding will use 22 characters of actual data and 2 characters of padding ('=')
        Span<char> base64 = stackalloc char[24];
        success = Convert.TryToBase64Chars(buffer, base64, out var written);
        Debug.Assert(success);
        Debug.Assert(written == 24);
        // Trim the two '=='
        Debug.Assert(base64.EndsWith("=="));
        return new string(base64[..^2]);
    }

    private Subscription? GetSubscription(string connectionId)
    {
        var connection = _connections[connectionId];
        return connection?.Features.Get<Subscription>();
    }
}