# Orleans.SignalR

Orleans.SignalR is a lightweight, open-source library that enables easy integration of SignalR with Orleans, a
distributed virtual actor model framework for building scalable, fault-tolerant systems.
The library provides a SignalR backplane, allowing you to effortlessly add real-time communication capabilities to your
distributed systems.

## Features

Orleans.SignalR offers the following features:

- Simple and clear implementation
- Seamless integration with SignalR
- Support for all SignalR functions, including ClientInvocation

## Installation

You can install Orleans.SignalR using Nuget

For Client

```
Install-Package ManagedCode.Orleans.SignalR.Client
```

For Server

```
Install-Package ManagedCode.Orleans.SignalR.Server
```

## Usage

### Client Configuration

To use Orleans.SignalR on the client, add the following code to your client configuration:

```csharp
clientBuilder.AddMemoryStreams(OrleansSignalROptions.DefaultSignalRStreamProvider);

clientBuilder.Services
    .AddSignalR()
    .AddOrleans();
```

### Server Configuration

To use Orleans.SignalR on the server, add the following code to your server configuration:

```csharp
siloBuilder
    .AddMemoryGrainStorage("PubSubStore")
    .AddMemoryStreams(OrleansSignalROptions.DefaultSignalRStreamProvider)
    .AddMemoryGrainStorage(OrleansSignalROptions.OrleansSignalRStorage);

siloBuilder.Services
    .AddSignalR()
    .AddOrleans();
```

### Using HubContext in Grain

In this example, TestGrain defines the interface for the grain, and TestGrain inject the interface for sending messages
using SignalR

```csharp
public class TestGrain : Grain, ITestGrain
{
    private readonly IHubContext<InterfaceTestHub> _hubContext;

    public TestGrain(IHubContext<InterfaceTestHub> hubContext)
    {
        _hubContext = hubContext;
    }

    public Task PushRandom()
    {
        return _hubContext.Clients.All.SendAsync("SendRandom", new Random().Next());
    }
}
```

### Using ClientInvocation

Orleans.SignalR supports all functions from SignalR, including ClientInvocation. However, to avoid blocking tasks,
use `Task.Run`.
Here's an example:

```csharp
public async Task<string> GetMessage(string connectionId)
{
    var message = await Task.Run(() => _hubContext.Clients.Client(connectionId)
        .InvokeAsync<string>("GetMessage", CancellationToken.None));
        
    return message;
}
```

## Contributing

We welcome contributions! If you encounter any bugs or have feature requests,
please [open an issue](https://github.com/managedcode/Orleans.SignalR/issues).
If you want to contribute code, please fork the repository and submit a pull request.

## License

Orleans.SignalR is licensed under the [MIT License](LICENSE).

## Credits

Orleans.SignalR is based on the work of the Orleans community and the SignalR team at Microsoft.