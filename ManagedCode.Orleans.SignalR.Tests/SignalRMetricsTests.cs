using System.Diagnostics.Metrics;
using ManagedCode.Orleans.SignalR.Core.Diagnostics;
using Shouldly;
using Xunit;

namespace ManagedCode.Orleans.SignalR.Tests;

public class SignalRMetricsTests
{
    private const string HubName = "hub";

    [Fact]
    public void RecordConnectionEstablished_EmitsCounter()
    {
        var measurements = new List<long>();

        using var listener = CreateListener(SignalRMetrics.ConnectionsTotalName, measurements.Add);
        SignalRMetrics.Instance.RecordConnectionEstablished(HubName);

        measurements.Sum().ShouldBe(1);
    }

    [Fact]
    public void RecordMessageSent_EmitsRecipientCount()
    {
        var measurements = new List<long>();

        using var listener = CreateListener(SignalRMetrics.MessagesSentTotalName, measurements.Add);
        SignalRMetrics.Instance.RecordMessageSent(HubName, SignalRMetrics.TargetTypes.Connections, 3);

        measurements.Sum().ShouldBe(3);
    }

    [Fact]
    public void GracePeriodCountersTrackStartAndEnd()
    {
        var measurements = new List<long>();

        using var listener = CreateListener(SignalRMetrics.ObserversInGracePeriodName, measurements.Add);
        SignalRMetrics.Instance.RecordGracePeriodStarted(HubName);
        SignalRMetrics.Instance.RecordGracePeriodEnded(HubName);

        measurements.ShouldBe(new List<long> { 1, -1 });
    }

    private static MeterListener CreateListener(string instrumentName, Action<long> onMeasurement)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, current) =>
            {
                if (instrument.Meter.Name == SignalRMetrics.MeterName &&
                    string.Equals(instrument.Name, instrumentName, StringComparison.Ordinal))
                {
                    current.EnableMeasurementEvents(instrument);
                }
            }
        };

        listener.SetMeasurementEventCallback<long>((_, measurement, _, _) => onMeasurement(measurement));
        listener.Start();
        return listener;
    }
}
