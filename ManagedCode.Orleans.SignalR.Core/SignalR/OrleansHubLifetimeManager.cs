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
using Microsoft.Extensions.Hosting;
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
        IHostApplicationLifetime hostLifetime, IOptions<OrleansSignalROptions> orleansSignalOptions,
        IOptions<HubOptions> globalHubOptions, IOptions<HubOptions<THub>> hubOptions)
    {
        _logger = logger;
        _orleansSignalOptions = orleansSignalOptions;
        _globalHubOptions = globalHubOptions;
        _hubOptions = hubOptions;
        _clusterClient = clusterClient;

        hostLifetime.ApplicationStopping.Register(OnApplicationStopping);
    }

    public override async Task OnConnectedAsync(HubConnectionContext connection)
    {
        _connections.Add(connection);
        var subscription = CreateConnectionObserver(connection);

        var hubKey = NameHelperGenerator.CleanString(typeof(THub).FullName!);
        var usePartitions = _orleansSignalOptions.Value.ConnectionPartitionCount > 1;
        var partitionId = 0;

        if (usePartitions)
        {
            var coordinatorGrain = NameHelperGenerator.GetConnectionCoordinatorGrain<THub>(_clusterClient);
            partitionId = await coordinatorGrain.GetPartitionForConnection(connection.ConnectionId);
            var partitionGrain = NameHelperGenerator.GetConnectionPartitionGrain<THub>(_clusterClient, partitionId);
            subscription.AddGrain(partitionGrain);
            await partitionGrain.AddConnection(connection.ConnectionId, subscription.Reference);
        }
        else
        {
            var connectionHolderGrain = NameHelperGenerator.GetConnectionHolderGrain<THub>(_clusterClient);
            subscription.AddGrain(connectionHolderGrain);
            await connectionHolderGrain.AddConnection(connection.ConnectionId, subscription.Reference);
        }

        subscription.SetConnectionMetadata(hubKey, usePartitions, partitionId);

        if (!string.IsNullOrEmpty(connection.UserIdentifier))
        {
            var userGrain = NameHelperGenerator.GetSignalRUserGrain<THub>(_clusterClient, connection.UserIdentifier!);
            subscription.AddGrain(userGrain);
            await userGrain.AddConnection(connection.ConnectionId, subscription.Reference);
            _ = Task.Run(userGrain.RequestMessage);
        }

        await UpdateConnectionHeartbeatAsync(connection.ConnectionId, subscription);
    }

    public override async Task OnDisconnectedAsync(HubConnectionContext connection)
    {
        _connections.Remove(connection);

        var subscription = connection.Features.Get<Subscription>();

        if (_orleansSignalOptions.Value.KeepEachConnectionAlive)
        {
            var hubKey = NameHelperGenerator.CleanString(typeof(THub).FullName!);
            var heartbeatGrain = NameHelperGenerator.GetConnectionHeartbeatGrain(_clusterClient, hubKey, connection.ConnectionId);
            await heartbeatGrain.Stop();
        }

        if (subscription is not null)
        {
            using (subscription)
            {
                var removalTasks = subscription.Grains
                    .Select(grain => grain.RemoveConnection(connection.ConnectionId, subscription.Reference))
                    .ToArray();

                if (removalTasks.Length > 0)
                {
                    await Task.WhenAll(removalTasks);
                }
            }

            connection.Features.Set<Subscription?>(null);
        }

        var coordinator = NameHelperGenerator.GetConnectionCoordinatorGrain<THub>(_clusterClient);
        await coordinator.NotifyConnectionRemoved(connection.ConnectionId);
    }

    public override Task SendAllAsync(string methodName, object?[] args, CancellationToken cancellationToken = new())
    {
        var message = new InvocationMessage(methodName, args);
        if (_orleansSignalOptions.Value.ConnectionPartitionCount > 1)
        {
            return Task.Run(() => NameHelperGenerator.GetConnectionCoordinatorGrain<THub>(_clusterClient).SendToAll(message), cancellationToken);
        }
        else
        {
            return Task.Run(() => NameHelperGenerator.GetConnectionHolderGrain<THub>(_clusterClient).SendToAll(message), cancellationToken);
        }
    }

    public override Task SendAllExceptAsync(string methodName, object?[] args,
        IReadOnlyList<string> excludedConnectionIds, CancellationToken cancellationToken = new())
    {
        var message = new InvocationMessage(methodName, args);
        if (_orleansSignalOptions.Value.ConnectionPartitionCount > 1)
        {
            return Task.Run(() => NameHelperGenerator.GetConnectionCoordinatorGrain<THub>(_clusterClient)
                .SendToAllExcept(message, excludedConnectionIds.ToArray()), cancellationToken);
        }
        else
        {
            return Task.Run(() => NameHelperGenerator.GetConnectionHolderGrain<THub>(_clusterClient)
                .SendToAllExcept(message, excludedConnectionIds.ToArray()), cancellationToken);
        }
    }

    public override Task SendConnectionAsync(string connectionId, string methodName, object?[] args,
        CancellationToken cancellationToken = new())
    {
        var message = new InvocationMessage(methodName, args);
        if (_orleansSignalOptions.Value.ConnectionPartitionCount > 1)
        {
            return Task.Run(() => NameHelperGenerator.GetConnectionCoordinatorGrain<THub>(_clusterClient)
                .SendToConnection(message, connectionId), cancellationToken);
        }
        else
        {
            return Task.Run(() => NameHelperGenerator.GetConnectionHolderGrain<THub>(_clusterClient)
                .SendToConnection(message, connectionId), cancellationToken);
        }
    }

    public override Task SendConnectionsAsync(IReadOnlyList<string> connectionIds, string methodName, object?[] args,
        CancellationToken cancellationToken = new())
    {
        var message = new InvocationMessage(methodName, args);
        if (_orleansSignalOptions.Value.ConnectionPartitionCount > 1)
        {
            return Task.Run(() => NameHelperGenerator.GetConnectionCoordinatorGrain<THub>(_clusterClient)
                .SendToConnections(message, connectionIds.ToArray()), cancellationToken);
        }
        else
        {
            return Task.Run(() => NameHelperGenerator.GetConnectionHolderGrain<THub>(_clusterClient)
                .SendToConnections(message, connectionIds.ToArray()), cancellationToken);
        }
    }

    public override Task SendGroupAsync(string groupName, string methodName, object?[] args,
        CancellationToken cancellationToken = new())
    {
        var message = new InvocationMessage(methodName, args);
        if (_orleansSignalOptions.Value.GroupPartitionCount > 1)
        {
            return Task.Run(() => NameHelperGenerator.GetGroupCoordinatorGrain<THub>(_clusterClient).SendToGroup(groupName, message), cancellationToken);
        }
        else
        {
            return Task.Run(() => NameHelperGenerator.GetSignalRGroupGrain<THub>(_clusterClient, groupName).SendToGroup(message), cancellationToken);
        }
    }

    public override Task SendGroupsAsync(IReadOnlyList<string> groupNames, string methodName, object?[] args,
        CancellationToken cancellationToken = new())
    {
        var message = new InvocationMessage(methodName, args);

        if (_orleansSignalOptions.Value.GroupPartitionCount > 1)
        {
            return Task.Run(() => NameHelperGenerator.GetGroupCoordinatorGrain<THub>(_clusterClient)
                .SendToGroups(groupNames.ToArray(), message), cancellationToken);
        }
        else
        {
            // For potentially many groups, use fire-and-forget to avoid memory issues
            Task.Run(() =>
            {
                foreach (var groupName in groupNames)
                {
                    var groupGrain = NameHelperGenerator.GetSignalRGroupGrain<THub>(_clusterClient, groupName);
                    _ = groupGrain.SendToGroup(message).ContinueWith(t =>
                    {
                        if (t.IsFaulted)
                        {
                            _logger.LogError(t.Exception, "Failed to send to group {GroupName}", groupName);
                        }
                    }, TaskContinuationOptions.OnlyOnFaulted);
                }
            }, cancellationToken);

            return Task.CompletedTask;
        }
    }

    public override Task SendGroupExceptAsync(string groupName, string methodName, object?[] args,
        IReadOnlyList<string> excludedConnectionIds, CancellationToken cancellationToken = new())
    {
        var message = new InvocationMessage(methodName, args);
        if (_orleansSignalOptions.Value.GroupPartitionCount > 1)
        {
            return Task.Run(() => NameHelperGenerator.GetGroupCoordinatorGrain<THub>(_clusterClient)
                .SendToGroupExcept(groupName, message, excludedConnectionIds.ToArray()), cancellationToken);
        }
        else
        {
            return Task.Run(() => NameHelperGenerator.GetSignalRGroupGrain<THub>(_clusterClient, groupName)
                .SendToGroupExcept(message, excludedConnectionIds.ToArray()), cancellationToken);
        }
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

        // For potentially many users, use fire-and-forget to avoid memory issues
        Task.Run(() =>
        {
            foreach (var userId in userIds)
            {
                var userGrain = NameHelperGenerator.GetSignalRUserGrain<THub>(_clusterClient, userId);
                _ = userGrain.SendToUser(message).ContinueWith(t =>
                {
                    if (t.IsFaulted)
                    {
                        _logger.LogError(t.Exception, "Failed to send to user {UserId}", userId);
                    }
                }, TaskContinuationOptions.OnlyOnFaulted);
            }
        }, cancellationToken);

        return Task.CompletedTask;
    }

    public override async Task AddToGroupAsync(string connectionId, string groupName,
        CancellationToken cancellationToken = new())
    {
        var subscription = GetSubscription(connectionId);
        if (subscription is null)
        {
            return;
        }

        if (_orleansSignalOptions.Value.GroupPartitionCount > 1)
        {
            var coordinatorGrain = NameHelperGenerator.GetGroupCoordinatorGrain<THub>(_clusterClient);
            var partitionId = await Task.Run(() => coordinatorGrain.GetPartitionForGroup(groupName), cancellationToken);
            var partitionGrain = NameHelperGenerator.GetGroupPartitionGrain<THub>(_clusterClient, partitionId);
            var hubKey = NameHelperGenerator.CleanString(typeof(THub).FullName!);

            await Task.Run(() => partitionGrain.EnsureInitialized(hubKey), cancellationToken);

            subscription.AddGrain(partitionGrain);
            await Task.Run(() => partitionGrain.AddConnection(connectionId, subscription.Reference), cancellationToken);
            await Task.Run(() => coordinatorGrain.AddConnectionToGroup(groupName, connectionId, subscription.Reference), cancellationToken);
        }
        else
        {
            var groupGrain = NameHelperGenerator.GetSignalRGroupGrain<THub>(_clusterClient, groupName);
            await Task.Run(() => groupGrain.AddConnection(connectionId, subscription.Reference), cancellationToken);
            subscription.AddGrain(groupGrain);
        }

        await UpdateConnectionHeartbeatAsync(connectionId, subscription);
    }

    public override async Task RemoveFromGroupAsync(string connectionId, string groupName,
        CancellationToken cancellationToken = new())
    {
        var subscription = GetSubscription(connectionId);
        if (subscription is null)
        {
            return;
        }

        if (_orleansSignalOptions.Value.GroupPartitionCount > 1)
        {
            var coordinatorGrain = NameHelperGenerator.GetGroupCoordinatorGrain<THub>(_clusterClient);
            var partitionId = await Task.Run(() => coordinatorGrain.GetPartitionForGroup(groupName), cancellationToken);
            var partitionGrain = NameHelperGenerator.GetGroupPartitionGrain<THub>(_clusterClient, partitionId);
            var hubKey = NameHelperGenerator.CleanString(typeof(THub).FullName!);

            await Task.Run(() => partitionGrain.EnsureInitialized(hubKey), cancellationToken);

            await Task.Run(() => coordinatorGrain.RemoveConnectionFromGroup(groupName, connectionId, subscription.Reference), cancellationToken);

            var stillTracked = await Task.Run(() => partitionGrain.HasConnection(connectionId), cancellationToken);
            if (!stillTracked)
            {
                subscription.RemoveGrain(partitionGrain);
                await UpdateConnectionHeartbeatAsync(connectionId, subscription);
            }
        }
        else
        {
            var groupGrain = NameHelperGenerator.GetSignalRGroupGrain<THub>(_clusterClient, groupName);
            await Task.Run(() => groupGrain.RemoveConnection(connectionId, subscription.Reference), cancellationToken);
            subscription.RemoveGrain(groupGrain);
            await UpdateConnectionHeartbeatAsync(connectionId, subscription);
        }
    }

    public override async Task<T> InvokeConnectionAsync<T>(string connectionId, string methodName, object?[] args,
        CancellationToken cancellationToken)
    {
        // send thing
        if (string.IsNullOrEmpty(connectionId))
        {
            throw new ArgumentNullException(nameof(connectionId));
        }

        var connection = _connections[connectionId];

        var invocationId = GenerateInvocationId();
        var invocationGrain = NameHelperGenerator.GetInvocationGrain<THub>(_clusterClient, invocationId);
        var invocationInfo = new InvocationInfo(connectionId, invocationId, typeof(T));

        TaskCompletionSource<T>? completionSource = null;
        Subscription? subscription = null;
        Task<CompletionMessage?>? completionTask = null;

        try
        {
            try
            {
                completionSource = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
                subscription = CreateSubscription(message =>
                {
                    if (message is CompletionMessage completionMessage)
                    {
                        if (completionMessage.HasResult)
                        {
                            completionSource.TrySetResult((T)completionMessage.Result!);
                        }
                        else
                        {
                            completionSource.TrySetException(new Exception(completionMessage.Error));
                        }
                    }

                    return Task.CompletedTask;
                });

                subscription.AddGrain(invocationGrain);
                await invocationGrain.AddInvocation(subscription.Reference, invocationInfo);
            }
            catch (InvalidOperationException ex) when (IsLocalObjectReferenceException(ex))
            {
                completionSource = null;
                subscription?.Dispose();
                subscription = null;

                await invocationGrain.AddInvocation(null, invocationInfo);
                completionTask = invocationGrain.WaitForCompletion();
            }

            var invocationMessage = new InvocationMessage(invocationId, methodName, args);

            if (connection == null)
            {
                // TODO: Need to handle other server going away while waiting for connection result
                var invocation = _orleansSignalOptions.Value.ConnectionPartitionCount > 1
                    ? await Task.Run(() => NameHelperGenerator.GetConnectionCoordinatorGrain<THub>(_clusterClient)
                        .SendToConnection(invocationMessage, connectionId))
                    : await Task.Run(() => NameHelperGenerator.GetConnectionHolderGrain<THub>(_clusterClient)
                        .SendToConnection(invocationMessage, connectionId));

                if (invocation == false)
                {
                    throw new IOException($"Connection '{connectionId}' does not exist.");
                }
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
                if (completionSource is not null)
                {
                    return await Task.Run(() => completionSource.Task, cancellationToken);
                }

                var completionMessage = await completionTask!.WaitAsync(cancellationToken) ?? throw new IOException($"Invocation '{invocationId}' returned no result for connection '{connectionId}'.");

                if (completionMessage.HasResult)
                {
                    return (T)completionMessage.Result!;
                }

                throw new Exception(completionMessage.Error);
            }
            catch
            {
                if (connection?.ConnectionAborted.IsCancellationRequested == true)
                {
                    throw new IOException($"Connection '{connectionId}' disconnected.");
                }

                throw;
            }
        }
        finally
        {
            if (subscription is not null && connection is not null)
            {
                var removalTasks = subscription.Grains
                    .Select(grain => grain.RemoveConnection(connection.ConnectionId, subscription.Reference))
                    .ToArray();

                if (removalTasks.Length > 0)
                {
                    await Task.WhenAll(removalTasks);
                }
            }

            await invocationGrain.RemoveInvocation();

            subscription?.Dispose();
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

        var timeSpan = TimeIntervalHelper.GetClientTimeoutInterval(_orleansSignalOptions, _globalHubOptions, _hubOptions);
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
        var subscription = new Subscription(new SignalRObserver(onNextAction));
        var reference = CreateObserverReference(subscription.GetObserver());
        subscription.SetReference(reference);
        return subscription;
    }

    private ISignalRObserver CreateObserverReference(SignalRObserver observer)
    {
        return _clusterClient.CreateObjectReference<ISignalRObserver>(observer);
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
            {
                if (!string.IsNullOrEmpty(invocation.InvocationId))
                {
                    await Task.Run(() => NameHelperGenerator.GetInvocationGrain<THub>(_clusterClient, invocation.InvocationId)
                        .TryCompleteResult(connection.ConnectionId,
                            CompletionMessage.WithError(invocation.InvocationId,
                                $"Connection disconnected. Reason:{ex.Message}")));
                }
            }

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

    private Task UpdateConnectionHeartbeatAsync(string connectionId, Subscription subscription)
    {
        if (!_orleansSignalOptions.Value.KeepEachConnectionAlive || string.IsNullOrEmpty(subscription.HubKey))
        {
            return Task.CompletedTask;
        }

        var hubKey = subscription.HubKey!;
        var heartbeatInterval = TimeIntervalHelper.GetClientTimeoutInterval(_orleansSignalOptions, _globalHubOptions, _hubOptions);
        var heartbeatGrain = NameHelperGenerator.GetConnectionHeartbeatGrain(_clusterClient, hubKey, connectionId);
        var registration = new ConnectionHeartbeatRegistration(
            hubKey,
            subscription.UsePartitioning,
            subscription.PartitionId,
            subscription.Reference,
            heartbeatInterval,
            subscription.GetGrainSnapshot());

        return heartbeatGrain.Start(registration);
    }

    private static bool IsLocalObjectReferenceException(Exception ex)
    {
        return ex is InvalidOperationException invalid &&
               invalid.Message.Contains("Cannot create a local object reference from a grain", StringComparison.OrdinalIgnoreCase);
    }

    private void OnApplicationStopping()
    {
        var tasks = new List<Task>(_connections.Count);

        foreach (var connection in _connections)
        {
            var subscription = connection.Features.Get<Subscription>();

            if (subscription is null)
            {
                return;
            }

            foreach (var grain in subscription.Grains)
            {
                tasks.Add(grain.RemoveConnection(connection.ConnectionId, subscription.Reference));
            }
        }

        _ = Task.Run(() => Task.WhenAll(tasks));
    }
}
