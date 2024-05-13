using ManagedCode.Orleans.SignalR.Core.Interfaces;
using Orleans;

namespace ManagedCode.Orleans.SignalR.Core.SignalR;

public static class NameHelperGenerator
{
    private static string ConnectionNamespace<TMessage>(string hub)
    {
        return $"{hub}.{typeof(TMessage).FullName}";
    }

    public static ISignalRConnectionHolderGrain GetConnectionHolderGrain<THub>(IGrainFactory grainFactory)
    {
        return grainFactory.GetGrain<ISignalRConnectionHolderGrain>(CleanString(typeof(THub).FullName!));
    }

    public static ISignalRInvocationGrain GetInvocationGrain<THub>(IGrainFactory grainFactory, string? invocationId)
    {
        return grainFactory.GetGrain<ISignalRInvocationGrain>(CleanString(typeof(THub).FullName + "::" + invocationId ?? "unknown"));
    }

    // public static ISignalRGroupHolderGrain GetGroupHolderGrain<THub>(IGrainFactory grainFactory)
    // {
    //     return grainFactory.GetGrain<ISignalRGroupHolderGrain>(typeof(THub).FullName);
    // }

    public static ISignalRUserGrain GetSignalRUserGrain<THub>(IGrainFactory grainFactory, string userId)
    {
        return grainFactory.GetGrain<ISignalRUserGrain>(CleanString(typeof(THub).FullName + "::" + userId));
    }

    public static ISignalRGroupGrain GetSignalRGroupGrain<THub>(IGrainFactory grainFactory, string groupId)
    {
        return grainFactory.GetGrain<ISignalRGroupGrain>(CleanString(typeof(THub).FullName + "::" + groupId));
    }

    public static string CleanString(string input)
    {
        var builder = new System.Text.StringBuilder();
        foreach (char c in input)
        {
            if (char.IsLetterOrDigit(c) || c == '-' || c == ':' || c == '.')
            {
                builder.Append(c);
            }
            else
            {
                builder.Append(":");
            }
        }
        return builder.ToString();
    }
    
    // public static IAsyncStream<TMessage> GetStream<THub, TMessage>(IClusterClient clusterClient,
    //     string streamProviderName, string streamName)
    // {
    //     var streamProvider = clusterClient.GetStreamProvider(streamProviderName);
    //     return GetStream<TMessage>(typeof(THub).FullName!, streamProvider, streamName);
    // }
    //
    // public static IAsyncStream<TMessage> GetStream<TMessage>(string hub, IStreamProvider streamProvider, string streamName)
    // {
    //     var streamId = StreamId.Create(ConnectionNamespace<TMessage>(hub), streamName);
    //     return streamProvider.GetStream<TMessage>(streamId);
    // }
}