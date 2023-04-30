using ManagedCode.Orleans.SignalR.Core.Interfaces;
using Orleans;
using Orleans.Runtime;
using Orleans.Streams;

namespace ManagedCode.Orleans.SignalR.Core.SignalR;

public static class NameHelperGenerator
{
    private static string ConnectionNamespace<THub, TMessage>()
    {
        return $"{typeof(THub).FullName}.{typeof(TMessage).FullName}";
    }

    public static ISignalRConnectionHolderGrain<THub> GetConnectionHolderGrain<THub>(IGrainFactory grainFactory)
    {
        return grainFactory.GetGrain<ISignalRConnectionHolderGrain<THub>>(typeof(THub).FullName);
    }

    public static ISignalRInvocationGrain<THub> GetInvocationGrain<THub>(IGrainFactory grainFactory,
        string invocationId)
    {
        return grainFactory.GetGrain<ISignalRInvocationGrain<THub>>(typeof(THub).FullName + "." + invocationId);
    }

    public static ISignalRGroupHolderGrain<THub> GetGroupHolderGrain<THub>(IGrainFactory grainFactory)
    {
        return grainFactory.GetGrain<ISignalRGroupHolderGrain<THub>>(typeof(THub).FullName);
    }

    public static ISignalRUserGrain<THub> GetSignalRUserGrain<THub>(IGrainFactory grainFactory, string userId)
    {
        return grainFactory.GetGrain<ISignalRUserGrain<THub>>(typeof(THub).FullName + "." + userId);
    }


    public static ISignalRGroupGrain<THub> GetSignalRGroupGrain<THub>(IGrainFactory grainFactory, string groupId)
    {
        return grainFactory.GetGrain<ISignalRGroupGrain<THub>>(typeof(THub).FullName + "." + groupId);
    }
    
    public static IAsyncStream<TMessage> GetStream<THub, TMessage>(IClusterClient clusterClient,
        string streamProviderName, string streamName)
    {
        var streamProvider = clusterClient.GetStreamProvider(streamProviderName);
        return GetStream<THub, TMessage>(streamProvider, streamName);
    }


    public static IAsyncStream<TMessage> GetStream<THub, TMessage>(IStreamProvider streamProvider, string streamName)
    {
        var streamId = StreamId.Create(ConnectionNamespace<THub, TMessage>(), streamName);
        return streamProvider.GetStream<TMessage>(streamId);
    }
}