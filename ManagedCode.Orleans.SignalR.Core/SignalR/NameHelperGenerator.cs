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
        return grainFactory.GetGrain<ISignalRConnectionHolderGrain>(Base64Encode(typeof(THub).FullName!));
    }

    public static ISignalRInvocationGrain GetInvocationGrain<THub>(IGrainFactory grainFactory, string invocationId)
    {
        return grainFactory.GetGrain<ISignalRInvocationGrain>(Base64Encode(typeof(THub).FullName + ":" + invocationId));
    }

    // public static ISignalRGroupHolderGrain GetGroupHolderGrain<THub>(IGrainFactory grainFactory)
    // {
    //     return grainFactory.GetGrain<ISignalRGroupHolderGrain>(typeof(THub).FullName);
    // }

    public static ISignalRUserGrain GetSignalRUserGrain<THub>(IGrainFactory grainFactory, string userId)
    {
        return grainFactory.GetGrain<ISignalRUserGrain>(Base64Encode(typeof(THub).FullName + ":" + userId));
    }

    public static ISignalRGroupGrain GetSignalRGroupGrain<THub>(IGrainFactory grainFactory, string groupId)
    {
        return grainFactory.GetGrain<ISignalRGroupGrain>(Base64Encode(typeof(THub).FullName + ":" + groupId));
    }

    public static string Base64Encode(string plainText) 
    {
        var plainTextBytes = System.Text.Encoding.UTF8.GetBytes(plainText);
        return System.Convert.ToBase64String(plainTextBytes);
    }

    public static string Base64Decode(string base64EncodedData) 
    {
        var base64EncodedBytes = System.Convert.FromBase64String(base64EncodedData);
        return System.Text.Encoding.UTF8.GetString(base64EncodedBytes);
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