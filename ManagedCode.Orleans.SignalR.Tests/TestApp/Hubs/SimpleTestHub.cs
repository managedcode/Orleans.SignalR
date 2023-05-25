using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR;

namespace ManagedCode.Orleans.SignalR.Tests.TestApp.Hubs;

public class SimpleTestHub : Hub
{
    public async Task<int> DoTest()
    {
        await Clients.Caller.SendAsync("DoTest", "test");
        return new Random().Next(1, 100);
    }

    public async Task<string> DoUser()
    {
        if (string.IsNullOrEmpty(Context.UserIdentifier))
            return "no";
        await Clients.User(Context.UserIdentifier).SendAsync("DoUser", Context.UserIdentifier);
        return Context.UserIdentifier;
    }

    public async Task AddToGroup(string groupName)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, groupName);
        await Task.Delay(TimeSpan.FromSeconds(1));
        await Clients.Group(groupName)
            .SendAsync("SendAll", $"{Context.ConnectionId} has joined the group {groupName}.");
    }

    public async Task RemoveFromGroup(string groupName)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, groupName);
    }

    public async Task GroupSendAsync(string groupName, string message)
    {
        await Clients.Group(groupName).SendAsync("SendAll", $"{Context.ConnectionId} send message: {message}.");
    }

    public async Task ManyGroupSendAsync(string[] groupNames, string message)
    {
        await Clients.Groups(groupNames).SendAsync("SendAll", $"{Context.ConnectionId} send message: {message}.");
    }

    public async Task SendGroupExceptAsync(string groupName, string message, string[] connections)
    {
        await Clients.GroupExcept(groupName, connections)
            .SendAsync("SendAll", $"{Context.ConnectionId} send message: {message}.");
    }

    public async IAsyncEnumerable<int> Counter(
        int count,
        int delay,
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

    public ChannelReader<int> CounterReader(
        int count,
        int delay,
        CancellationToken cancellationToken)
    {
        var channel = Channel.CreateUnbounded<int>();

        // We don't want to await WriteItemsAsync, otherwise we'd end up waiting 
        // for all the items to be written before returning the channel back to
        // the client.
        _ = WriteItemsAsync(channel.Writer, count, delay, cancellationToken);

        return channel.Reader;
    }

    private async Task WriteItemsAsync(
        ChannelWriter<int> writer,
        int count,
        int delay,
        CancellationToken cancellationToken)
    {
        Exception localException = null;
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
            if (!TestWebApplication.StaticLogs.ContainsKey(nameof(UploadStream)))
                TestWebApplication.StaticLogs[nameof(UploadStream)] = new ConcurrentQueue<string>();

            TestWebApplication.StaticLogs[nameof(UploadStream)].Enqueue(item);
        }
    }

    public async Task UploadStreamChannelReader(ChannelReader<string> stream)
    {
        while (await stream.WaitToReadAsync())
        while (stream.TryRead(out var item))
        {
            if (!TestWebApplication.StaticLogs.ContainsKey(nameof(UploadStreamChannelReader)))
                TestWebApplication.StaticLogs[nameof(UploadStreamChannelReader)] = new ConcurrentQueue<string>();

            TestWebApplication.StaticLogs[nameof(UploadStreamChannelReader)].Enqueue(item);
        }
    }

    public async Task<int> All()
    {
        await Clients.All.SendAsync("SendAll", "test");
        return new Random().Next(1, 100);
    }

    public async Task<int> Connections(string[] connections)
    {
        await Clients.Clients(connections).SendAsync("SendAll", "test");
        return new Random().Next(1, 100);
    }

    public async Task<int> Others()
    {
        await Clients.Others.SendAsync("SendAll", "test");
        return new Random().Next(1, 100);
    }

    public async Task<int> AllExcept(string[] connectionIds)
    {
        await Clients.AllExcept(connectionIds).SendAsync("SendAll", "test");
        return new Random().Next(1, 100);
    }

    public async Task SentToUser(string userId, string message)
    {
        await Clients.User(userId).SendAsync("SendAll", message);
    }

    public async Task SentToUserIds(string[] userIds, string message)
    {
        await Clients.Users(userIds).SendAsync("SendAll", message);
    }
}