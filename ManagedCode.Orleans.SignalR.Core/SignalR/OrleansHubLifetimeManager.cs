using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ManagedCode.Orleans.SignalR.Core.Config;
using ManagedCode.Orleans.SignalR.Core.Diagnostics;
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
using Orleans.Runtime;

namespace ManagedCode.Orleans.SignalR.Core.SignalR;

public class OrleansHubLifetimeManager<THub> : HubLifetimeManager<THub> where THub : Hub
{
    private const double HeartbeatRenewalIntervalDivisor = 2;
    private const double MinimumHeartbeatRenewalIntervalMilliseconds = 500;
    private const double MinimumHeartbeatRenewalWarningIntervalSeconds = 5;
    private const string InvocationReturnTypeHeader = "ManagedCode.Orleans.SignalR.ReturnType";
    private readonly IClusterClient _clusterClient;
    private readonly HubConnectionStore _connections = new();
    private readonly ConcurrentDictionary<string, InvocationReturnType> _invocationReturnTypes = new(StringComparer.Ordinal);
    private readonly IOptions<HubOptions> _globalHubOptions;
    private readonly IOptions<HubOptions<THub>> _hubOptions;
    private readonly ILogger _logger;
    private readonly IOptions<OrleansSignalROptions> _orleansSignalOptions;
    private readonly SignalRMetrics _metrics = SignalRMetrics.Instance;
    private readonly string _hubKey;
    private long _lastHeartbeatRenewalWarningTimestamp;

    public OrleansHubLifetimeManager(ILogger<OrleansHubLifetimeManager<THub>> logger, IClusterClient clusterClient,
        IHostApplicationLifetime hostLifetime, IOptions<OrleansSignalROptions> orleansSignalOptions,
        IOptions<HubOptions> globalHubOptions, IOptions<HubOptions<THub>> hubOptions)
    {
        _logger = logger;
        _orleansSignalOptions = orleansSignalOptions;
        _globalHubOptions = globalHubOptions;
        _hubOptions = hubOptions;
        _clusterClient = clusterClient;
        _hubKey = NameHelperGenerator.CleanString(typeof(THub).FullName!);

        hostLifetime.ApplicationStopping.Register(OnApplicationStopping);
        if (_orleansSignalOptions.Value.KeepEachConnectionAlive)
        {
            _ = Task.Run(() => RunConnectionHeartbeatRenewalAsync(hostLifetime.ApplicationStopping));
        }
    }

    public override async Task OnConnectedAsync(HubConnectionContext connection)
    {
        _connections.Add(connection);
        var subscription = CreateConnectionObserver(connection);

        try
        {

            var usePartitions = _orleansSignalOptions.Value.ConnectionPartitionCount > 1;
            var partitionId = 0;

            // Retry logic for silo restart scenarios where grain directory has stale entries
            const int maxRetries = 3;
            for (var attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
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

                    // Success - break out of retry loop
                    break;
                }
                catch (OrleansMessageRejectionException ex) when (attempt < maxRetries)
                {
                    // Silo was restarted - grain directory has stale entries
                    // Wait briefly and retry as the new silo should activate fresh grains
                    _logger.LogWarning(ex,
                        "Grain call failed on attempt {Attempt}/{MaxRetries} for connection {ConnectionId}, retrying after delay",
                        attempt, maxRetries, connection.ConnectionId);
                    await Task.Delay(100 * attempt); // Exponential backoff: 100ms, 200ms
                    subscription.ClearGrains();
                }
            }

            subscription.SetConnectionMetadata(_hubKey, usePartitions, partitionId);

            if (!string.IsNullOrEmpty(connection.UserIdentifier))
            {
                try
                {
                    var userGrain = NameHelperGenerator.GetSignalRUserGrain<THub>(_clusterClient, connection.UserIdentifier!);
                    subscription.AddGrain(userGrain);
                    await userGrain.AddConnection(connection.ConnectionId, subscription.Reference);
                    _ = Task.Run(userGrain.RequestMessage);
                }
                catch (OrleansMessageRejectionException ex)
                {
                    _logger.LogWarning(ex, "Failed to register user grain for connection {ConnectionId}", connection.ConnectionId);
                    // Continue - connection can still work without user-specific messaging
                }
            }

            await UpdateConnectionHeartbeatAsync(connection.ConnectionId, subscription);
            _metrics.RecordConnectionEstablished(_hubKey);
        }
        catch
        {
            await CleanupFailedConnectionAsync(connection, subscription);
            throw;
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Best-effort rollback must preserve the original connection failure.")]
    private async Task CleanupFailedConnectionAsync(HubConnectionContext connection, Subscription subscription)
    {
        _connections.Remove(connection);

        if (_orleansSignalOptions.Value.KeepEachConnectionAlive)
        {
            try
            {
                var heartbeatGrain = NameHelperGenerator.GetConnectionHeartbeatGrain(_clusterClient, _hubKey, connection.ConnectionId);
                await heartbeatGrain.Stop();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to stop heartbeat for partially registered connection {ConnectionId}.", connection.ConnectionId);
            }
        }

        try
        {
            var removalTasks = subscription.Grains
                .Select(grain => SafeRemoveConnectionAsync(grain, connection.ConnectionId, subscription.Reference))
                .ToArray();

            if (removalTasks.Length > 0)
            {
                await Task.WhenAll(removalTasks);
            }

            var coordinator = NameHelperGenerator.GetConnectionCoordinatorGrain<THub>(_clusterClient);
            await coordinator.NotifyConnectionRemoved(connection.ConnectionId);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to clean up partially registered connection {ConnectionId}.", connection.ConnectionId);
        }
        finally
        {
            DisposeSubscription(subscription);
            connection.Features.Set<Subscription?>(null);
        }
    }

    public override async Task OnDisconnectedAsync(HubConnectionContext connection)
    {
        _connections.Remove(connection);
        RemoveInvocationReturnTypes(connection.ConnectionId);
        _metrics.RecordConnectionClosed(_hubKey);

        var subscription = connection.Features.Get<Subscription>();

        if (_orleansSignalOptions.Value.KeepEachConnectionAlive)
        {
            try
            {
                var heartbeatGrain = NameHelperGenerator.GetConnectionHeartbeatGrain(_clusterClient, _hubKey, connection.ConnectionId);
                await heartbeatGrain.Stop();
            }
            catch (OrleansMessageRejectionException ex)
            {
                // Silo was restarted - heartbeat grain no longer exists
                _logger.LogDebug(ex, "Heartbeat grain unavailable during disconnect for {ConnectionId}", connection.ConnectionId);
            }
        }

        if (subscription is not null)
        {
            try
            {
                try
                {
                    var removalTasks = subscription.Grains
                        .Select(grain => SafeRemoveConnectionAsync(grain, connection.ConnectionId, subscription.Reference))
                        .ToArray();

                    if (removalTasks.Length > 0)
                    {
                        await Task.WhenAll(removalTasks);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to remove connections from grains during disconnect for {ConnectionId}", connection.ConnectionId);
                }
            }
            finally
            {
                // Target removals and heartbeat Stop are one-way, but only use the observer identity for cleanup.
                DisposeSubscription(subscription);
                connection.Features.Set<Subscription?>(null);
            }
        }

        try
        {
            var coordinator = NameHelperGenerator.GetConnectionCoordinatorGrain<THub>(_clusterClient);
            await coordinator.NotifyConnectionRemoved(connection.ConnectionId);
        }
        catch (OrleansMessageRejectionException ex)
        {
            // Silo was restarted - coordinator grain will be fresh anyway
            _logger.LogDebug(ex, "Coordinator grain unavailable during disconnect for {ConnectionId}", connection.ConnectionId);
        }
    }

    private static async Task SafeRemoveConnectionAsync(IObserverConnectionManager grain, string connectionId, ISignalRObserver reference)
    {
        try
        {
            await grain.RemoveConnection(connectionId, reference);
        }
        catch (OrleansMessageRejectionException)
        {
            // Grain was on old silo - nothing to clean up
        }
    }

    public override Task SendAllAsync(string methodName, object?[] args, CancellationToken cancellationToken = new())
    {
        var message = new InvocationMessage(methodName, args);
        _metrics.RecordMessageSent(_hubKey, SignalRMetrics.TargetTypes.All);
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
        _metrics.RecordMessageSent(_hubKey, SignalRMetrics.TargetTypes.AllExcept);
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
        _metrics.RecordMessageSent(_hubKey, SignalRMetrics.TargetTypes.Connection);
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
        _metrics.RecordMessageSent(_hubKey, SignalRMetrics.TargetTypes.Connections, connectionIds.Count);
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
        _metrics.RecordMessageSent(_hubKey, SignalRMetrics.TargetTypes.Group);
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
        _metrics.RecordMessageSent(_hubKey, SignalRMetrics.TargetTypes.Groups, groupNames.Count);

        if (_orleansSignalOptions.Value.GroupPartitionCount > 1)
        {
            return Task.Run(() => NameHelperGenerator.GetGroupCoordinatorGrain<THub>(_clusterClient)
                .SendToGroups(groupNames.ToArray(), message), cancellationToken);
        }

        // Fire-and-forget to avoid blocking hub execution on large group fan-out.
        _ = Task.Run(async () =>
        {
            foreach (var groupName in groupNames)
            {
                try
                {
                    var groupGrain = NameHelperGenerator.GetSignalRGroupGrain<THub>(_clusterClient, groupName);
                    await groupGrain.SendToGroup(message);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to send to group {GroupName}", groupName);
                }
            }
        }, cancellationToken);

        return Task.CompletedTask;
    }

    public override Task SendGroupExceptAsync(string groupName, string methodName, object?[] args,
        IReadOnlyList<string> excludedConnectionIds, CancellationToken cancellationToken = new())
    {
        var message = new InvocationMessage(methodName, args);
        _metrics.RecordMessageSent(_hubKey, SignalRMetrics.TargetTypes.GroupExcept);
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
        _metrics.RecordMessageSent(_hubKey, SignalRMetrics.TargetTypes.User);
        return Task.Run(() => NameHelperGenerator.GetSignalRUserGrain<THub>(_clusterClient, userId).SendToUser(message), cancellationToken);
    }

    public override Task SendUsersAsync(IReadOnlyList<string> userIds, string methodName, object?[] args,
        CancellationToken cancellationToken = new())
    {
        var message = new InvocationMessage(methodName, args);
        _metrics.RecordMessageSent(_hubKey, SignalRMetrics.TargetTypes.Users, userIds.Count);

        // Fire-and-forget to avoid blocking hub execution on large user fan-out.
        _ = Task.Run(async () =>
        {
            foreach (var userId in userIds)
            {
                try
                {
                    var userGrain = NameHelperGenerator.GetSignalRUserGrain<THub>(_clusterClient, userId);
                    await userGrain.SendToUser(message);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to send to user {UserId}", userId);
                }
            }
        }, cancellationToken);

        return Task.CompletedTask;
    }

    public override async Task AddToGroupAsync(string connectionId, string groupName,
        CancellationToken cancellationToken = new())
    {
        await AddToGroupsAsync(connectionId, [groupName], cancellationToken);
    }

    public async Task AddToGroupsAsync(string connectionId, IReadOnlyList<string> groupNames,
        CancellationToken cancellationToken = default)
    {
        var subscription = GetSubscription(connectionId);
        if (subscription is null)
        {
            return;
        }

        var uniqueGroupNames = GetUniqueGroupNames(groupNames);
        if (uniqueGroupNames.Length == 0)
        {
            return;
        }

        var subscriptionReference = subscription.Reference;

        if (_orleansSignalOptions.Value.GroupPartitionCount > 1)
        {
            var coordinatorGrain = NameHelperGenerator.GetGroupCoordinatorGrain<THub>(_clusterClient);
            var partitionIds = await Task.Run(
                () => coordinatorGrain.AddConnectionToGroups(uniqueGroupNames, connectionId, subscriptionReference),
                cancellationToken);

            if (IsConnectionDisconnected(connectionId))
            {
                await CleanupDisconnectedBatchPartitionMembershipAsync(
                    coordinatorGrain,
                    uniqueGroupNames,
                    connectionId,
                    subscriptionReference,
                    cancellationToken);
                return;
            }

            foreach (var partitionId in partitionIds)
            {
                var partitionGrain = NameHelperGenerator.GetGroupPartitionGrain<THub>(_clusterClient, partitionId);
                subscription.AddGrain(partitionGrain);
            }

            if (IsConnectionDisconnected(connectionId))
            {
                await CleanupDisconnectedBatchPartitionMembershipAsync(
                    coordinatorGrain,
                    uniqueGroupNames,
                    connectionId,
                    subscriptionReference,
                    cancellationToken);
                return;
            }
        }
        else
        {
            var groupGrains = uniqueGroupNames
                .Select(groupName => NameHelperGenerator.GetSignalRGroupGrain<THub>(_clusterClient, groupName))
                .Distinct()
                .ToArray();

            var tasks = groupGrains
                .Select(groupGrain => Task.Run(() => groupGrain.AddConnection(connectionId, subscriptionReference), cancellationToken))
                .ToArray();

            if (tasks.Length > 0)
            {
                await Task.WhenAll(tasks);
            }

            if (IsConnectionDisconnected(connectionId))
            {
                var cleanupTasks = groupGrains
                    .Select(groupGrain => Task.Run(() => groupGrain.RemoveConnection(connectionId, subscriptionReference), cancellationToken))
                    .ToArray();

                if (cleanupTasks.Length > 0)
                {
                    await Task.WhenAll(cleanupTasks);
                }

                return;
            }

            foreach (var groupGrain in groupGrains)
            {
                subscription.AddGrain(groupGrain);
            }

            if (IsConnectionDisconnected(connectionId))
            {
                var cleanupTasks = groupGrains
                    .Select(groupGrain => Task.Run(() => groupGrain.RemoveConnection(connectionId, subscriptionReference), cancellationToken))
                    .ToArray();

                if (cleanupTasks.Length > 0)
                {
                    await Task.WhenAll(cleanupTasks);
                }

                return;
            }
        }

        await UpdateConnectionHeartbeatAsync(connectionId, subscription);
    }

    public override async Task RemoveFromGroupAsync(string connectionId, string groupName,
        CancellationToken cancellationToken = new())
    {
        await RemoveFromGroupsAsync(connectionId, [groupName], cancellationToken);
    }

    public async Task RemoveFromGroupsAsync(string connectionId, IReadOnlyList<string> groupNames,
        CancellationToken cancellationToken = default)
    {
        var subscription = GetSubscription(connectionId);
        if (subscription is null)
        {
            return;
        }

        var uniqueGroupNames = GetUniqueGroupNames(groupNames);
        if (uniqueGroupNames.Length == 0)
        {
            return;
        }

        var subscriptionReference = subscription.Reference;

        if (_orleansSignalOptions.Value.GroupPartitionCount > 1)
        {
            var coordinatorGrain = NameHelperGenerator.GetGroupCoordinatorGrain<THub>(_clusterClient);
            var partitionIds = await Task.Run(
                () => coordinatorGrain.RemoveConnectionFromGroups(uniqueGroupNames, connectionId, subscriptionReference),
                cancellationToken);

            foreach (var partitionId in partitionIds)
            {
                var partitionGrain = NameHelperGenerator.GetGroupPartitionGrain<THub>(_clusterClient, partitionId);
                var stillTracked = await Task.Run(() => partitionGrain.HasConnection(connectionId), cancellationToken);
                if (!stillTracked)
                {
                    subscription.RemoveGrain(partitionGrain);
                }
            }

            await UpdateConnectionHeartbeatAsync(connectionId, subscription);
        }
        else
        {
            var groupGrains = uniqueGroupNames
                .Select(groupName => NameHelperGenerator.GetSignalRGroupGrain<THub>(_clusterClient, groupName))
                .Distinct()
                .ToArray();

            var tasks = groupGrains
                .Select(groupGrain => Task.Run(() => groupGrain.RemoveConnection(connectionId, subscriptionReference), cancellationToken))
                .ToArray();

            if (tasks.Length > 0)
            {
                await Task.WhenAll(tasks);
            }

            foreach (var groupGrain in groupGrains)
            {
                subscription.RemoveGrain(groupGrain);
            }

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

        try
        {
            await invocationGrain.AddInvocation(null, invocationInfo);
            var completionTask = invocationGrain.WaitForCompletion();

            var invocationMessage = new InvocationMessage(invocationId, methodName, args);

            if (connection == null)
            {
                // TODO: Need to handle other server going away while waiting for connection result
                AttachInvocationReturnType(invocationMessage, typeof(T));
                var invocation = _orleansSignalOptions.Value.ConnectionPartitionCount > 1
                    ? await Task.Run(() => NameHelperGenerator.GetConnectionCoordinatorGrain<THub>(_clusterClient)
                        .SendToConnection(invocationMessage, connectionId), cancellationToken)
                    : await Task.Run(() => NameHelperGenerator.GetConnectionHolderGrain<THub>(_clusterClient)
                        .SendToConnection(invocationMessage, connectionId), cancellationToken);

                if (invocation == false)
                {
                    throw new IOException($"Connection '{connectionId}' does not exist.");
                }
            }
            else
            {
                _invocationReturnTypes[invocationId] = new InvocationReturnType(typeof(T), connectionId);
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
                var completionMessage = await completionTask.WaitAsync(cancellationToken) ?? throw new IOException($"Invocation '{invocationId}' returned no result for connection '{connectionId}'.");

                if (completionMessage.HasResult)
                {
                    return (T)completionMessage.Result!;
                }

                throw new HubException(completionMessage.Error);
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
            _invocationReturnTypes.TryRemove(invocationId, out _);
            await invocationGrain.RemoveInvocation();
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Observer reference deletion is best-effort during connection teardown.")]
    private void DisposeSubscription(Subscription subscription)
    {
        try
        {
            subscription.DisposeReference(_clusterClient.DeleteObjectReference<ISignalRObserver>);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to delete Orleans observer object reference for hub {HubKey}.", _hubKey);
        }
    }

    public override async Task SetConnectionResultAsync(string connectionId, CompletionMessage result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (!string.IsNullOrEmpty(result.InvocationId))
        {
            _invocationReturnTypes.TryRemove(result.InvocationId, out _);
        }

        await Task.Run(() => NameHelperGenerator.GetInvocationGrain<THub>(_clusterClient, result.InvocationId)
            .TryCompleteResult(connectionId, result));
    }

    public override bool TryGetReturnType(string invocationId, [NotNullWhen(true)] out Type? type)
    {
        if (_invocationReturnTypes.TryGetValue(invocationId, out var registration))
        {
            type = registration.Type;
            return true;
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
        if (message is InvocationMessage { InvocationId: not null, Headers: not null } routedInvocation &&
            routedInvocation.Headers.TryGetValue(InvocationReturnTypeHeader, out var returnTypeName))
        {
            routedInvocation.Headers.Remove(InvocationReturnTypeHeader);
            if (routedInvocation.Headers.Count == 0)
            {
                routedInvocation.Headers = null;
            }

            _invocationReturnTypes[routedInvocation.InvocationId] = new InvocationReturnType(
                Type.GetType(returnTypeName, throwOnError: true)!,
                connection.ConnectionId);
        }

        try
        {
            // Critical: SignalR writes must not execute on Orleans' serial observer dispatcher.
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

    private static void AttachInvocationReturnType(InvocationMessage invocationMessage, Type returnType)
    {
        invocationMessage.Headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [InvocationReturnTypeHeader] = returnType.AssemblyQualifiedName ?? returnType.FullName ?? returnType.Name
        };
    }

    private void RemoveInvocationReturnTypes(string connectionId)
    {
        foreach (var (invocationId, registration) in _invocationReturnTypes)
        {
            if (string.Equals(registration.ConnectionId, connectionId, StringComparison.Ordinal))
            {
                _invocationReturnTypes.TryRemove(invocationId, out _);
            }
        }
    }

    private Subscription? GetSubscription(string connectionId)
    {
        var connection = _connections[connectionId];
        return connection?.Features.Get<Subscription>();
    }

    private static string[] GetUniqueGroupNames(IReadOnlyList<string> groupNames)
    {
        ArgumentNullException.ThrowIfNull(groupNames);

        if (groupNames.Count == 0)
        {
            return [];
        }

        var unique = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<string>(groupNames.Count);

        foreach (var groupName in groupNames)
        {
            if (unique.Add(groupName))
            {
                ordered.Add(groupName);
            }
        }

        return ordered.ToArray();
    }

    private static async Task CleanupDisconnectedBatchPartitionMembershipAsync(
        ISignalRGroupCoordinatorGrain coordinatorGrain,
        string[] groupNames,
        string connectionId,
        ISignalRObserver subscriptionReference,
        CancellationToken cancellationToken)
    {
        await Task.Run(
            () => coordinatorGrain.RemoveConnectionFromGroups(groupNames, connectionId, subscriptionReference),
            cancellationToken);
    }

    private bool IsConnectionDisconnected(string connectionId)
    {
        var connection = _connections[connectionId];
        return connection is null || connection.ConnectionAborted.IsCancellationRequested;
    }

    private Task UpdateConnectionHeartbeatAsync(string connectionId, Subscription subscription)
    {
        if (!TryCreateHeartbeatRegistration(connectionId, subscription, out var heartbeatGrain, out var registration))
        {
            return Task.CompletedTask;
        }

        return heartbeatGrain.Start(registration);
    }

    private bool TryCreateHeartbeatRegistration(
        string connectionId,
        Subscription subscription,
        out ISignalRConnectionHeartbeatGrain heartbeatGrain,
        out ConnectionHeartbeatRegistration registration)
    {
        var hubKey = subscription.HubKey;
        var observer = subscription.Reference;
        if (!_orleansSignalOptions.Value.KeepEachConnectionAlive ||
            string.IsNullOrEmpty(hubKey) ||
            observer is null ||
            IsConnectionDisconnected(connectionId))
        {
            heartbeatGrain = default!;
            registration = default!;
            return false;
        }

        var heartbeatInterval = TimeIntervalHelper.GetClientTimeoutInterval(
            _orleansSignalOptions,
            _globalHubOptions,
            _hubOptions);
        heartbeatGrain = NameHelperGenerator.GetConnectionHeartbeatGrain(_clusterClient, hubKey, connectionId);
        registration = new ConnectionHeartbeatRegistration(
            hubKey,
            subscription.UsePartitioning,
            subscription.PartitionId,
            observer,
            heartbeatInterval,
            subscription.GetHeartbeatGrainIds(),
            connectionId);
        return true;
    }

    private async Task RunConnectionHeartbeatRenewalAsync(CancellationToken cancellationToken)
    {
        var heartbeatInterval = TimeIntervalHelper.GetClientTimeoutInterval(
            _orleansSignalOptions,
            _globalHubOptions,
            _hubOptions);
        if (heartbeatInterval <= TimeSpan.Zero)
        {
            return;
        }

        var renewalInterval = TimeSpan.FromMilliseconds(Math.Max(
            MinimumHeartbeatRenewalIntervalMilliseconds,
            heartbeatInterval.TotalMilliseconds / HeartbeatRenewalIntervalDivisor));
        using var timer = new PeriodicTimer(renewalInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                var renewals = new List<Task>();
                foreach (var connection in _connections)
                {
                    var subscription = connection.Features.Get<Subscription>();
                    if (subscription is not null &&
                        TryCreateHeartbeatRegistration(
                            connection.ConnectionId,
                            subscription,
                            out var heartbeatGrain,
                            out var registration))
                    {
                        renewals.Add(RenewConnectionHeartbeatAsync(connection.ConnectionId, heartbeatGrain, registration));
                    }
                }

                if (renewals.Count > 0)
                {
                    await Task.WhenAll(renewals);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "One failed connection must not terminate heartbeat renewal for every connection on the host.")]
    private async Task RenewConnectionHeartbeatAsync(
        string connectionId,
        ISignalRConnectionHeartbeatGrain heartbeatGrain,
        ConnectionHeartbeatRegistration registration)
    {
        try
        {
            await heartbeatGrain.Start(registration);
        }
        catch (Exception ex)
        {
            _metrics.RecordHeartbeatRenewalFailure(_hubKey, ex.GetType().Name);
            if (ShouldLogHeartbeatRenewalWarning())
            {
                _logger.LogWarning(ex,
                    "Failed to renew heartbeat lease for connection {ConnectionId}; the lease was not extended and will be retried.",
                    connectionId);
            }
            else
            {
                _logger.LogDebug(ex, "Failed to renew heartbeat lease for connection {ConnectionId}.", connectionId);
            }
        }
    }

    private bool ShouldLogHeartbeatRenewalWarning()
    {
        var warningInterval = TimeIntervalHelper.GetClientTimeoutInterval(
            _orleansSignalOptions,
            _globalHubOptions,
            _hubOptions);
        warningInterval = TimeSpan.FromSeconds(Math.Max(
            MinimumHeartbeatRenewalWarningIntervalSeconds,
            warningInterval.TotalSeconds));

        while (true)
        {
            var now = Stopwatch.GetTimestamp();
            var lastWarning = Volatile.Read(ref _lastHeartbeatRenewalWarningTimestamp);
            if (lastWarning != 0 && Stopwatch.GetElapsedTime(lastWarning, now) < warningInterval)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _lastHeartbeatRenewalWarningTimestamp, now, lastWarning) == lastWarning)
            {
                return true;
            }
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Shutdown cleanup is best-effort and must continue for the remaining connections.")]
    private void OnApplicationStopping()
    {
        foreach (var connection in _connections)
        {
            var subscription = connection.Features.Get<Subscription>();

            if (subscription is null)
            {
                continue;
            }

            try
            {
                var reference = subscription.Reference;
                foreach (var grain in subscription.Grains)
                {
                    _ = grain.RemoveConnection(connection.ConnectionId, reference);
                }

                if (_orleansSignalOptions.Value.KeepEachConnectionAlive)
                {
                    var heartbeatGrain = NameHelperGenerator.GetConnectionHeartbeatGrain(_clusterClient, _hubKey, connection.ConnectionId);
                    _ = heartbeatGrain.Stop();
                }

                var coordinator = NameHelperGenerator.GetConnectionCoordinatorGrain<THub>(_clusterClient);
                _ = coordinator.NotifyConnectionRemoved(connection.ConnectionId);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to schedule shutdown cleanup for connection {ConnectionId}.", connection.ConnectionId);
            }
            finally
            {
                DisposeSubscription(subscription);
                connection.Features.Set<Subscription?>(null);
            }
        }
    }

    private readonly record struct InvocationReturnType(Type Type, string ConnectionId);
}
