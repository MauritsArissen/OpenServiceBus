using System.Diagnostics;
using System.Diagnostics.Metrics;
using Azure.Messaging.ServiceBus;
using OpenServiceBus.Core.Diagnostics;
using OpenServiceBus.Core.Entities;

namespace OpenServiceBus.IntegrationTests;

/// <summary>
/// The broker emits OpenTelemetry-shaped <see cref="ActivitySource"/> spans and
/// <see cref="Meter"/> measurements on the send / receive / settle / DLQ paths. Tests subscribe
/// via <see cref="ActivityListener"/> and <see cref="MeterListener"/> - the same way an OTel
/// pipeline would - and assert the conventional tag values.
/// </summary>
public class DiagnosticsTests
{
    [Fact]
    public async Task SendAndComplete_EmitsSendReceiveAndSettleActivitiesWithMessagingTags()
    {
        // Arrange - capture every activity from OpenServiceBus's source for the duration.
        var captured = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = src => src.Name == OpenServiceBusDiagnostics.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => { lock (captured) captured.Add(a); },
        };
        ActivitySource.AddActivityListener(listener);

        await using var harness = await IntegrationHarness.StartAsync();
        await harness.Queues.CreateAsync(new QueueDescriptor { Name = "diag-q" });
        await using var client = new ServiceBusClient(harness.ConnectionString);

        // Act
        await client.CreateSender("diag-q").SendMessageAsync(new ServiceBusMessage("hi") { MessageId = "m1" });
        var receiver = client.CreateReceiver("diag-q");
        var msg = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(5));
        msg.ShouldNotBeNull();
        await receiver.CompleteMessageAsync(msg);

        // Assert - one span per phase. Note: the topic-fanout test below covers the publish/fanout shape;
        // here we only care that send + receive + settle each surface once with the right tags.
        // ActivitySource is process-global; other parallel tests may be emitting too. Filter to
        // our queue so the assertions don't flake under parallel test execution.
        //
        // Timing: the broker settles the wire disposition BEFORE its spans are disposed on the
        // IO thread, so CompleteMessageAsync can return microseconds before ActivityStopped
        // fires (observed as a release-pipeline flake). Poll briefly instead of asserting the
        // instant the client returns.
        bool ForOurQueue(Activity a) => "diag-q".Equals(a.GetTagItem(OpenServiceBusDiagnostics.TagDestination));
        List<Activity> ours = [];
        Dictionary<string, List<Activity>> byName = [];
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            lock (captured)
            {
                ours = captured.Where(ForOurQueue).ToList();
            }
            byName = ours.GroupBy(a => a.OperationName).ToDictionary(g => g.Key, g => g.ToList());
            if (byName.ContainsKey(OpenServiceBusDiagnostics.SpanSend)
                && byName.ContainsKey(OpenServiceBusDiagnostics.SpanReceive)
                && byName.ContainsKey(OpenServiceBusDiagnostics.SpanSettle))
            {
                break;
            }
            await Task.Delay(50);
        }

        byName.ShouldContainKey(OpenServiceBusDiagnostics.SpanSend);
        byName.ShouldContainKey(OpenServiceBusDiagnostics.SpanReceive);
        byName.ShouldContainKey(OpenServiceBusDiagnostics.SpanSettle);

        var send = byName[OpenServiceBusDiagnostics.SpanSend].First();
        send.GetTagItem(OpenServiceBusDiagnostics.TagSystem).ShouldBe("servicebus");
        send.GetTagItem(OpenServiceBusDiagnostics.TagDestination).ShouldBe("diag-q");
        send.GetTagItem(OpenServiceBusDiagnostics.TagMessageId).ShouldBe("m1");
        send.Kind.ShouldBe(ActivityKind.Producer);

        var settle = byName[OpenServiceBusDiagnostics.SpanSettle].First();
        settle.GetTagItem(OpenServiceBusDiagnostics.TagDestination).ShouldBe("diag-q");
        settle.GetTagItem(OpenServiceBusDiagnostics.TagDisposition).ShouldBe("complete");
        settle.Kind.ShouldBe(ActivityKind.Consumer);
    }

    [Fact]
    public async Task DeadLetterMessage_IncrementsDeadLetteredCounterWithReasonTag()
    {
        // Arrange - listen for the dead-letter counter and record (delta, tags) tuples.
        var captures = new List<(long Value, IReadOnlyDictionary<string, object?> Tags)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == OpenServiceBusDiagnostics.SourceName
                    && instrument.Name == "osb.messages.deadlettered")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            var dict = new Dictionary<string, object?>();
            foreach (var t in tags) dict[t.Key] = t.Value;
            lock (captures) captures.Add((value, dict));
        });
        listener.Start();

        await using var harness = await IntegrationHarness.StartAsync();
        await harness.Queues.CreateAsync(new QueueDescriptor { Name = "dlq-q" });
        await using var client = new ServiceBusClient(harness.ConnectionString);

        // Act
        await client.CreateSender("dlq-q").SendMessageAsync(new ServiceBusMessage("die") { MessageId = "x" });
        var receiver = client.CreateReceiver("dlq-q");
        var msg = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(5));
        msg.ShouldNotBeNull();
        await receiver.DeadLetterMessageAsync(msg, deadLetterReason: "boom", deadLetterErrorDescription: "manual");

        // Assert - filter to our queue (Meter is process-global; parallel tests may DLQ too).
        var ours = captures.Where(c => "dlq-q".Equals(c.Tags[OpenServiceBusDiagnostics.TagDeadLetterSource])).ToList();
        ours.ShouldNotBeEmpty();
        ours.Sum(c => c.Value).ShouldBe(1L);
        ours[0].Tags[OpenServiceBusDiagnostics.TagDeadLetterReason].ShouldBe("boom");
    }

    [Fact]
    public async Task QueueDepthGauge_ObservedAfterEnqueue_ReportsCurrentCount()
    {
        // Arrange - capture the queue.depth gauge readings on demand.
        var readings = new List<(long Value, IReadOnlyDictionary<string, object?> Tags)>();
        Instrument? depthInstrument = null;
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == OpenServiceBusDiagnostics.SourceName
                    && instrument.Name == "osb.queue.depth")
                {
                    depthInstrument = instrument;
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            var dict = new Dictionary<string, object?>();
            foreach (var t in tags) dict[t.Key] = t.Value;
            lock (readings) readings.Add((value, dict));
        });
        listener.Start();

        await using var harness = await IntegrationHarness.StartAsync();
        await harness.Queues.CreateAsync(new QueueDescriptor { Name = "depth-q" });

        // Act - enqueue two messages then ask the listener to poll the gauge.
        await using var client = new ServiceBusClient(harness.ConnectionString);
        var sender = client.CreateSender("depth-q");
        await sender.SendMessageAsync(new ServiceBusMessage("1"));
        await sender.SendMessageAsync(new ServiceBusMessage("2"));

        // Force one gauge collection cycle for the observable instrument.
        listener.RecordObservableInstruments();

        // Assert - at least the main queue's reading should show 2.
        var depthQReadings = readings
            .Where(r => "depth-q".Equals(r.Tags[OpenServiceBusDiagnostics.TagDestination]))
            .ToList();
        depthQReadings.ShouldNotBeEmpty();
        depthQReadings.Last().Value.ShouldBe(2L);
    }
}
