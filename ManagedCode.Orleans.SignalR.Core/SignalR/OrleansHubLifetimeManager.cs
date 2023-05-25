using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ManagedCode.Orleans.SignalR.Core.Config;
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
    private readonly IOptions<HubOptions>? _globalHubOptions;
    private readonly IOptions<HubOptions<THub>>? _hubOptions;
    private readonly IHubProtocolResolver _hubProtocolResolver;

    private readonly ILogger _logger;
    private readonly IOptions<OrleansSignalROptions> _options;

    public OrleansHubLifetimeManager(ILogger<OrleansHubLifetimeManager<THub>> logger,
        IOptions<OrleansSignalROptions> options, IClusterClient clusterClient, IHubProtocolResolver hubProtocolResolver,
        IOptions<HubOptions>? globalHubOptions, IOptions<HubOptions<THub>>? hubOptions)
    {
        _logger = logger;
        _options = options;
        _clusterClient = clusterClient;
        _hubProtocolResolver = hubProtocolResolver;
        _globalHubOptions = globalHubOptions;
        _hubOptions = hubOptions;
    }

    public override async Task OnConnectedAsync(HubConnectionContext connection)
    {
        var connectionHolderGrain = NameHelperGenerator.GetConnectionHolderGrain<THub>(_clusterClient);

        var subscription = CreateConnectionObserver(connection);

        //Subscribe the instance to receive messages.
        await connectionHolderGrain.AddConnection(connection.ConnectionId, subscription.Reference)
            .ConfigureAwait(false);
        subscription.Grains.Add(connectionHolderGrain);

        if (!string.IsNullOrEmpty(connection.UserIdentifier))
        {
            var userGrain = NameHelperGenerator.GetSignalRUserGrain<THub>(_clusterClient, connection.UserIdentifier!);
            await userGrain.AddConnection(connection.ConnectionId, subscription.Reference).ConfigureAwait(false);
            subscription.Grains.Add(userGrain);
        }

        _connections.Add(connection);
    }

    public override Task OnDisconnectedAsync(HubConnectionContext connection)
    {
        _connections.Remove(connection);

        var subscription = connection.Features.Get<Subscription>();

        // If the bus is null then the Redis connection failed to be established and none of the other connection setup ran
        if (subscription is null)
            return Task.CompletedTask;

        var tasks = new List<Task>(subscription.Grains.Count);

        foreach (var grain in subscription.Grains)
            tasks.Add(grain.RemoveConnection(connection.ConnectionId, subscription.Reference));

        return Task.WhenAll(tasks);
    }

    public override Task SendAllAsync(string methodName, object?[] args, CancellationToken cancellationToken = new())
    {
        var message = new InvocationMessage(methodName, args);
        return NameHelperGenerator.GetConnectionHolderGrain<THub>(_clusterClient).SendToAll(message);
    }

    public override Task SendAllExceptAsync(string methodName, object?[] args,
        IReadOnlyList<string> excludedConnectionIds, CancellationToken cancellationToken = new())
    {
        var message = new InvocationMessage(methodName, args);
        return NameHelperGenerator.GetConnectionHolderGrain<THub>(_clusterClient)
            .SendToAllExcept(message, excludedConnectionIds.ToArray());
    }

    public override Task SendConnectionAsync(string connectionId, string methodName, object?[] args,
        CancellationToken cancellationToken = new())
    {
        var message = new InvocationMessage(methodName, args);
        return NameHelperGenerator.GetConnectionHolderGrain<THub>(_clusterClient)
            .SendToConnection(message, connectionId);
    }

    public override Task SendConnectionsAsync(IReadOnlyList<string> connectionIds, string methodName, object?[] args,
        CancellationToken cancellationToken = new())
    {
        var message = new InvocationMessage(methodName, args);
        return NameHelperGenerator.GetConnectionHolderGrain<THub>(_clusterClient)
            .SendToConnections(message, connectionIds.ToArray());
    }

    public override Task SendGroupAsync(string groupName, string methodName, object?[] args,
        CancellationToken cancellationToken = new())
    {
        var message = new InvocationMessage(methodName, args);
        return NameHelperGenerator.GetSignalRGroupGrain<THub>(_clusterClient, groupName).SendToGroup(message);
    }

    public override Task SendGroupsAsync(IReadOnlyList<string> groupNames, string methodName, object?[] args,
        CancellationToken cancellationToken = new())
    {
        var message = new InvocationMessage(methodName, args);
        var tasks = new List<Task>(groupNames.Count);

        foreach (var groupName in groupNames)
            tasks.Add(NameHelperGenerator.GetSignalRGroupGrain<THub>(_clusterClient, groupName).SendToGroup(message));

        return Task.WhenAll(tasks);
    }

    public override Task SendGroupExceptAsync(string groupName, string methodName, object?[] args,
        IReadOnlyList<string> excludedConnectionIds, CancellationToken cancellationToken = new())
    {
        var message = new InvocationMessage(methodName, args);
        return NameHelperGenerator.GetSignalRGroupGrain<THub>(_clusterClient, groupName)
            .SendToGroupExcept(message, excludedConnectionIds.ToArray());
    }

    public override Task SendUserAsync(string userId, string methodName, object?[] args,
        CancellationToken cancellationToken = new())
    {
        var message = new InvocationMessage(methodName, args);
        return NameHelperGenerator.GetSignalRUserGrain<THub>(_clusterClient, userId).SendToUser(message);
    }

    public override Task SendUsersAsync(IReadOnlyList<string> userIds, string methodName, object?[] args,
        CancellationToken cancellationToken = new())
    {
        var message = new InvocationMessage(methodName, args);
        var tasks = new List<Task>(userIds.Count);
        foreach (var userId in userIds)
            tasks.Add(NameHelperGenerator.GetSignalRUserGrain<THub>(_clusterClient, userId).SendToUser(message));

        return Task.WhenAll(tasks);
    }

    public override async Task AddToGroupAsync(string connectionId, string groupName,
        CancellationToken cancellationToken = new())
    {
        var subscription = GetSubscription(connectionId);
        if (subscription is null)
            return;

        var groupGrain = NameHelperGenerator.GetSignalRGroupGrain<THub>(_clusterClient, groupName);
        await groupGrain.AddConnection(connectionId, subscription.Reference).ConfigureAwait(false);

        subscription.Grains.Add(groupGrain);
    }

    public override async Task RemoveFromGroupAsync(string connectionId, string groupName,
        CancellationToken cancellationToken = new())
    {
        var subscription = GetSubscription(connectionId);
        if (subscription is null)
            return;

        var groupGrain = NameHelperGenerator.GetSignalRGroupGrain<THub>(_clusterClient, groupName);
        await groupGrain.RemoveConnection(connectionId, subscription.Reference).ConfigureAwait(false);

        subscription.Grains.Remove(groupGrain);
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

        var subscription = CreateSubscription(message =>
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
        subscription.Grains.Add(invocationGrain);
        await invocationGrain.AddInvocation(subscription.Reference,
            new InvocationInfo(connectionId, invocationId, typeof(T)));

        var invocationMessage = new InvocationMessage(invocationId, methodName, args);

        if (connection == null)
        {
            // TODO: Need to handle other server going away while waiting for connection result
            var invocation = await NameHelperGenerator.GetConnectionHolderGrain<THub>(_clusterClient)
                .SendToConnection(invocationMessage, connectionId).ConfigureAwait(false);

            if (invocation == false)
                throw new IOException($"Connection '{connectionId}' does not exist.");
        }
        else
        {
            // We're sending to a single connection
            // Write message directly to connection without caching it in memory
            _ = Task.Run(() =>
            {
                try
                {
                    return connection.WriteAsync(invocationMessage, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "InvokeConnectionAsync connection {ConnectionConnectionId} failed",
                        connection.ConnectionId);
                    throw;
                }
            }, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            return await Task.Run(() => tcs.Task, cancellationToken).ConfigureAwait(false);
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
            foreach (var grain in subscription.Grains)
                _ = grain.RemoveConnection(connection?.ConnectionId, subscription.Reference).ConfigureAwait(false);
            subscription.Dispose();
        }
    }

    public override async Task SetConnectionResultAsync(string connectionId, CompletionMessage result)
    {
        await NameHelperGenerator.GetInvocationGrain<THub>(_clusterClient, result.InvocationId)
            .TryCompleteResult(connectionId, result).ConfigureAwait(false);
    }

    public override bool TryGetReturnType(string invocationId, [NotNullWhen(true)] out Type? type)
    {
        var result = NameHelperGenerator.GetInvocationGrain<THub>(_clusterClient, invocationId).TryGetReturnType()
            .Result;

        type = result.GetReturnType();
        return result.Result;
    }

    private Subscription CreateConnectionObserver(HubConnectionContext connection)
    {
        var subscription = CreateSubscription(message => OnNextAsync(connection, message));
        connection.Features.Set(subscription);
        return subscription;
    }

    private Subscription CreateSubscription(Func<HubMessage, Task>? onNextAction)
    {
        var subscription = new Subscription(new SignalRObserver(onNextAction),
            _globalHubOptions.Value.KeepAliveInterval.Value);
        var reference = _clusterClient.CreateObjectReference<ISignalRObserver>(subscription.GetObserver());
        subscription.SetReference(reference);
        return subscription;
    }

    private async Task OnNextAsync(HubConnectionContext connection, HubMessage message)
    {
        try
        {
            await connection.WriteAsync(message).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (message is InvocationMessage invocation)
                if (!string.IsNullOrEmpty(invocation.InvocationId))
                    await NameHelperGenerator.GetInvocationGrain<THub>(_clusterClient, invocation.InvocationId)
                        .TryCompleteResult(connection.ConnectionId,
                            CompletionMessage.WithError(invocation.InvocationId,
                                $"Connection disconnected. Reason:{ex.Message}")).ConfigureAwait(false);

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