using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR;

namespace ManagedCode.Orleans.SignalR.Tests.TestApp.Hubs;

public class StressTestHub : Hub
{
    public async Task AddToGroup(string groupName)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, groupName);
    }

    public async Task RemoveFromGroup(string groupName)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, groupName);
    }

    public async Task GroupSendAsync(string groupName, string message)
    {
        await Clients.Group(groupName).SendAsync("GroupSendAsync", $"{Context.ConnectionId} send message: {message}.");
    }

    public async Task ManyGroupSendAsync(string[] groupNames, string message)
    {
        await Clients.Groups(groupNames)
            .SendAsync("ManyGroupSendAsync", $"{Context.ConnectionId} send message: {message}.");
    }

    public async Task SendGroupExceptAsync(string groupName, string message, string[] connections)
    {
        await Clients.GroupExcept(groupName, connections)
            .SendAsync("SendGroupExceptAsync", $"{Context.ConnectionId} send message: {message}.");
    }

    public async IAsyncEnumerable<int> Counter(int count, int delay,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var i = 0; i < count; i++)
        {
            // Check the cancellation token regularly so that the server will stop
            // producing items if the client disconnects.
            cancellationToken.ThrowIfCancellationRequested();

            yield return i;

            // Use the cancellationToken in other APIs that accept cancellation
            // tokens so the cancellation can flow down to them.
            await Task.Delay(delay, cancellationToken);
        }
    }

    public ChannelReader<int> CounterReader(int count, int delay, CancellationToken cancellationToken)
    {
        var channel = Channel.CreateUnbounded<int>();

        // We don't want to await WriteItemsAsync, otherwise we'd end up waiting 
        // for all the items to be written before returning the channel back to
        // the client.
        _ = WriteItemsAsync(channel.Writer, count, delay, cancellationToken);

        return channel.Reader;
    }

    private static async Task WriteItemsAsync(ChannelWriter<int> writer, int count, int delay,
        CancellationToken cancellationToken)
    {
        Exception? localException = null;
        try
        {
            for (var i = 0; i < count; i++)
            {
                await writer.WriteAsync(i, cancellationToken);

                // Use the cancellationToken in other APIs that accept cancellation
                // tokens so the cancellation can flow down to them.
                await Task.Delay(delay, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            localException = ex;
        }
        finally
        {
            writer.Complete(localException);
        }
    }

    public async Task UploadStream(IAsyncEnumerable<string> stream)
    {
        await foreach (var item in stream)
        {
            if (!TestWebApplication.StaticLogs.TryGetValue(nameof(UploadStream), out var value))
            {
                value = new ConcurrentQueue<string>();
                TestWebApplication.StaticLogs[nameof(UploadStream)] = value;
            }

            value.Enqueue(item);
        }
    }

    public async Task UploadStreamChannelReader(ChannelReader<string> stream)
    {
        while (await stream.WaitToReadAsync())
        {
            while (stream.TryRead(out var item))
            {
                if (!TestWebApplication.StaticLogs.TryGetValue(nameof(UploadStreamChannelReader), out var value))
                {
                    value = new ConcurrentQueue<string>();
                    TestWebApplication.StaticLogs[nameof(UploadStreamChannelReader)] = value;
                }

                value.Enqueue(item);
            }
        }
    }

    public async Task<int> All()
    {
        await Clients.All.SendAsync("All", "test");
        return new Random().Next(1, 100);
    }

    public async Task BroadcastPayload(string payload)
    {
        await Clients.All.SendAsync("PerfBroadcast", payload);
    }

    public async Task<int> Connections(string[] connections)
    {
        await Clients.Clients(connections).SendAsync("Connections", "test");
        return new Random().Next(1, 100);
    }

    public async Task<int> Others()
    {
        await Clients.Others.SendAsync("Others", "test");
        return new Random().Next(1, 100);
    }

    public async Task<int> AllExcept(string[] connectionIds)
    {
        await Clients.AllExcept(connectionIds).SendAsync("AllExcept", "test");
        return new Random().Next(1, 100);
    }

    public async Task SentToUser(string userId, string message)
    {
        await Clients.User(userId).SendAsync("SentToUser", message);
    }

    public async Task SentToUserIds(string[] userIds, string message)
    {
        await Clients.Users(userIds).SendAsync("SentToUserIds", message);
    }
}
