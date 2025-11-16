using System.Collections.Concurrent;
using System.Text.Json;

namespace ManagedCode.Orleans.SignalR.Tests.Infrastructure;

public static class PerformanceSummaryRecorder
{
    private sealed record ScenarioRun(string Implementation, bool UseOrleans, double DurationMilliseconds, double Throughput, DateTimeOffset Timestamp);

    private sealed class ScenarioSummary
    {
        public ScenarioSummary(string key, string displayName)
        {
            Key = key;
            DisplayName = displayName;
        }

        public string Key { get; }
        public string DisplayName { get; }
        public ScenarioRun? Orleans { get; private set; }
        public ScenarioRun? InMemory { get; private set; }

        public void Record(ScenarioRun run)
        {
            if (run.UseOrleans)
            {
                Orleans = run;
            }
            else
            {
                InMemory = run;
            }
        }

        public double? DeltaMilliseconds =>
            Orleans is not null && InMemory is not null
                ? Orleans.DurationMilliseconds - InMemory.DurationMilliseconds
                : null;

        public double? Ratio =>
            Orleans is not null && InMemory is not null && InMemory.DurationMilliseconds > 0
                ? Orleans.DurationMilliseconds / InMemory.DurationMilliseconds
                : null;
    }

    private static readonly ConcurrentDictionary<string, ScenarioSummary> Summaries = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static void RecordRun(string scenarioKey, string displayName, bool useOrleans, TimeSpan duration, double throughput)
    {
        var run = new ScenarioRun(
            useOrleans ? "Orleans" : "In-Memory",
            useOrleans,
            duration.TotalMilliseconds,
            throughput,
            DateTimeOffset.UtcNow);

        var summary = Summaries.GetOrAdd(scenarioKey, key => new ScenarioSummary(key, displayName));
        summary.Record(run);

        WriteSummaries();
    }

    private static void WriteSummaries()
    {
        var path = GetSummaryPath();
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var payload = Summaries.Values
            .OrderBy(summary => summary.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(summary => new
            {
                summary.DisplayName,
                summary.Key,
                Orleans = summary.Orleans,
                InMemory = summary.InMemory,
                summary.DeltaMilliseconds,
                summary.Ratio
            });

        File.WriteAllText(path, JsonSerializer.Serialize(payload, JsonOptions));
    }

    private static string GetSummaryPath()
    {
        var env = Environment.GetEnvironmentVariable("ORLEANS_SIGNALR_PERF_SUMMARY");
        if (!string.IsNullOrEmpty(env))
        {
            return env;
        }

        return Path.Combine(AppContext.BaseDirectory, "perf-summary.json");
    }
}
