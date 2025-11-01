using ManagedCode.Orleans.SignalR.Tests.Cluster;
using ManagedCode.Orleans.SignalR.Tests.Infrastructure;
using ManagedCode.Orleans.SignalR.Tests.Infrastructure.Logging;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace ManagedCode.Orleans.SignalR.Tests;

[Collection(nameof(LoadCluster))]
public class PerformanceComparisonTests
{
    private readonly PerformanceScenarioHarness _harness;
    private readonly PerformanceScenarioSettings _settings;
    private readonly ITestOutputHelper _output;

    public PerformanceComparisonTests(LoadClusterFixture cluster, ITestOutputHelper output)
    {
        var loggerAccessor = new TestOutputHelperAccessor { Output = output };
        _output = output;
        _harness = new PerformanceScenarioHarness(cluster, output, loggerAccessor, PerformanceScenarioSettings.CreatePerformance());
        _settings = _harness.Settings;
    }

    [Fact]
    public async Task Device_Echo_Performance_Comparison()
    {
        var orleans = await _harness.RunDeviceEchoAsync(useOrleans: true, basePort: 9400);
        var inMemory = await _harness.RunDeviceEchoAsync(useOrleans: false, basePort: 9500);

        AssertValidDurations("Device echo", orleans, inMemory);
    }

    [Fact]
    public async Task Broadcast_Fanout_Performance_Comparison()
    {
        var orleans = await _harness.RunBroadcastFanoutAsync(useOrleans: true, basePort: 9600);
        var inMemory = await _harness.RunBroadcastFanoutAsync(useOrleans: false, basePort: 9700);

        AssertValidDurations("Broadcast", orleans, inMemory);
    }

    [Fact]
    public async Task Group_Broadcast_Performance_Comparison()
    {
        var orleans = await _harness.RunGroupScenarioAsync(useOrleans: true, basePort: 9800);
        var inMemory = await _harness.RunGroupScenarioAsync(useOrleans: false, basePort: 9900);

        AssertValidDurations("Group", orleans, inMemory);
    }

    [Fact]
    public async Task Streaming_Performance_Comparison()
    {
        var orleans = await _harness.RunStreamingScenarioAsync(useOrleans: true, basePort: 10_000);
        var inMemory = await _harness.RunStreamingScenarioAsync(useOrleans: false, basePort: 10_100);

        AssertValidDurations("Streaming", orleans, inMemory);
    }

    [Fact]
    public async Task Invocation_Performance_Comparison()
    {
        var orleans = await _harness.RunInvocationScenarioAsync(useOrleans: true, basePort: 10_200);
        var inMemory = await _harness.RunInvocationScenarioAsync(useOrleans: false, basePort: 10_300);

        AssertValidDurations("Invocation", orleans, inMemory);
    }

    private void AssertValidDurations(string scenario, TimeSpan orleans, TimeSpan inMemory)
    {
        orleans.ShouldBeGreaterThan(TimeSpan.Zero, $"{scenario} Orleans run should have a non-zero duration.");
        inMemory.ShouldBeGreaterThan(TimeSpan.Zero, $"{scenario} in-memory run should have a non-zero duration.");

        var delta = orleans - inMemory;
        var ratio = inMemory.TotalMilliseconds == 0 ? double.PositiveInfinity : orleans.TotalMilliseconds / inMemory.TotalMilliseconds;

        _output.WriteLine(
            $"{scenario} comparison => Orleans: {orleans.TotalMilliseconds:F0} ms, In-Memory: {inMemory.TotalMilliseconds:F0} ms, Δ={delta.TotalMilliseconds:F0} ms, ratio={ratio:F2}×.");
    }
}
