using Microsoft.Extensions.Logging;

namespace ManagedCode.Orleans.SignalR.Tests.Infrastructure.Logging;

internal sealed class XunitLoggerProvider(ITestOutputHelperAccessor accessor) : ILoggerProvider, ISupportExternalScope
{
    private readonly ITestOutputHelperAccessor _accessor = accessor ?? throw new ArgumentNullException(nameof(accessor));
    private IExternalScopeProvider _scopeProvider = new LoggerExternalScopeProvider();

    public ILogger CreateLogger(string categoryName) => new XunitLogger(categoryName, _accessor, _scopeProvider);

    public void Dispose()
    {
        // Nothing to dispose
    }

    public void SetScopeProvider(IExternalScopeProvider scopeProvider)
    {
        _scopeProvider = scopeProvider ?? new LoggerExternalScopeProvider();
    }

    private sealed class XunitLogger(
        string categoryName,
        ITestOutputHelperAccessor accessor,
        IExternalScopeProvider scopeProvider) : ILogger
    {
        private readonly string _categoryName = categoryName;
        private readonly ITestOutputHelperAccessor _accessor = accessor;
        private readonly IExternalScopeProvider _scopeProvider = scopeProvider;

        public IDisposable BeginScope<TState>(TState state) where TState : notnull
        {
            return _scopeProvider?.Push(state) ?? DisposableScope.Instance;
        }

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            var output = _accessor.Output;
            if (output is null)
            {
                return;
            }

            var message = formatter(state, exception);
            if (string.IsNullOrWhiteSpace(message) && exception is null)
            {
                return;
            }

            var timestamp = DateTimeOffset.UtcNow.ToString("O");
            output.WriteLine($"[{timestamp}] {_categoryName} [{logLevel}] {message}");

            if (exception is not null)
            {
                output.WriteLine(exception.ToString());
            }
        }
    }

    private sealed class DisposableScope : IDisposable
    {
        public static readonly DisposableScope Instance = new();

        public void Dispose()
        {
        }
    }
}
