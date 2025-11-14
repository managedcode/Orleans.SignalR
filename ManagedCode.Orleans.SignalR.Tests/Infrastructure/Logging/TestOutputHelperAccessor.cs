using Xunit.Abstractions;

namespace ManagedCode.Orleans.SignalR.Tests.Infrastructure.Logging;

public sealed class TestOutputHelperAccessor : ITestOutputHelperAccessor
{
    private readonly AsyncLocal<ITestOutputHelper?> _current = new();

    public ITestOutputHelper? Output
    {
        get => _current.Value;
        set => _current.Value = value;
    }
}
