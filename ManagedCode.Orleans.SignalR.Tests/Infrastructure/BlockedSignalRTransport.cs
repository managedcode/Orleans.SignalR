using System.IO.Pipelines;
using System.Net;
using ManagedCode.Orleans.SignalR.Core.SignalR.Observers;
using ManagedCode.Orleans.SignalR.Tests.TestApp.Hubs;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace ManagedCode.Orleans.SignalR.Tests.Infrastructure;

internal sealed class BlockedSignalRTransport(IServiceProvider services) : IConnectionFactory, IAsyncDisposable
{
    private readonly Pipe _incoming = new();
    private readonly Pipe _outgoing = new();
    private readonly FlushGate _gate = new();
    private Task? _server;
    private DefaultConnectionContext? _serverConnection;

    public HubConnection CreateClient()
    {
        var builder = new HubConnectionBuilder().WithUrl("http://localhost/write-completion");
        builder.Services.AddSingleton<IConnectionFactory>(this);
        return builder.Build();
    }

    public Task Entered => _gate.Entered;

    public void BlockNextFlush() => _gate.BlockNextFlush();

    public void Release() => _gate.Release();

    public ValueTask<ConnectionContext> ConnectAsync(EndPoint endpoint, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var id = Guid.NewGuid().ToString("N");
        _serverConnection = new DefaultConnectionContext(id)
        {
            Transport = new DuplexPipe(_incoming.Reader, new GatedPipeWriter(_outgoing.Writer, _gate))
        };
        var client = new DefaultConnectionContext(id)
        {
            Transport = new DuplexPipe(_outgoing.Reader, _incoming.Writer)
        };
        _server = Task.Run(() => services.GetRequiredService<HubConnectionHandler<SimpleTestHub>>()
            .OnConnectedAsync(_serverConnection), cancellationToken);
        return ValueTask.FromResult<ConnectionContext>(client);
    }

    public async ValueTask DisposeAsync()
    {
        Release();
        await _incoming.Writer.CompleteAsync();
        if (_server is not null)
        {
            await _server.WaitAsync(TimeSpan.FromSeconds(10));
        }
        await _outgoing.Reader.CompleteAsync();
        if (_serverConnection is not null)
        {
            await _serverConnection.DisposeAsync();
        }
    }

    private sealed class DuplexPipe(PipeReader input, PipeWriter output) : IDuplexPipe
    {
        public PipeReader Input { get; } = input;
        public PipeWriter Output { get; } = output;
    }

    private sealed class FlushGate
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _blockNext;

        public Task Entered => _entered.Task;

        public void BlockNextFlush() => Interlocked.Exchange(ref _blockNext, 1);

        public void Release() => _released.TrySetResult();

        public async ValueTask WaitAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _blockNext, 0) == 0)
            {
                return;
            }
            _entered.TrySetResult();
            await _released.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class GatedPipeWriter(PipeWriter inner, FlushGate gate) : PipeWriter
    {
        public override void Advance(int bytes) => inner.Advance(bytes);
        public override Memory<byte> GetMemory(int sizeHint = 0) => inner.GetMemory(sizeHint);
        public override Span<byte> GetSpan(int sizeHint = 0) => inner.GetSpan(sizeHint);
        public override void CancelPendingFlush() => inner.CancelPendingFlush();
        public override void Complete(Exception? exception = null) => inner.Complete(exception);
        public override async ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
        {
            var result = await inner.FlushAsync(cancellationToken);
            await gate.WaitAsync(cancellationToken);
            return result;
        }
    }
}

internal sealed class ConnectionObserverCapture : IHubFilter
{
    public TaskCompletionSource<SignalRObserver> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task OnConnectedAsync(HubLifetimeContext context, Func<HubLifetimeContext, Task> next)
    {
        await next(context);
        var subscription = context.Context.Features.Get<Subscription>()
            ?? throw new InvalidOperationException("The real Orleans connection observer is missing.");
        Ready.TrySetResult(subscription.GetObserver());
    }
}
