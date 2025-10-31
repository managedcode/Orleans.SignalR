using Xunit.Abstractions;

namespace ManagedCode.Orleans.SignalR.Tests.Infrastructure.Logging;

public interface ITestOutputHelperAccessor
{
    ITestOutputHelper? Output { get; set; }
}
