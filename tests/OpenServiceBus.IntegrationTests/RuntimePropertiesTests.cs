using System.Xml.Linq;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Microsoft.Extensions.Time.Testing;
using OpenServiceBus.Core.Messaging;
using OpenServiceBus.Core.Storage;
using OpenServiceBus.InMemoryStorage;
using OpenServiceBus.Testing;

namespace OpenServiceBus.IntegrationTests;

/// <summary>
/// Runtime properties on the ATOM management surface (issue #60): topic-level counters
/// aggregated across subscriptions, AccessedAt fed from the activity tracker, rule CreatedAt
/// round-trips, and the no-materialization guarantee behind the list endpoints.
/// </summary>
public class RuntimePropertiesTests
{
    private const string SbNs = "http://schemas.microsoft.com/netservices/2010/10/servicebus/connect";

    private static async Task<XDocument> GetAtomXmlAsync(int port, string path)
    {
        using var http = new HttpClient();
        var body = await http.GetStringAsync($"http://127.0.0.1:{port}/{path}?api-version=2021-05");
        return XDocument.Parse(body);
    }

    private static long CountDetail(XContainer description, string localName) =>
        long.Parse(description.Descendants().Single(e => e.Name.LocalName == localName).Value);

    [Fact]
    public async Task GetTopicRuntimePropertiesAsync_AggregatesCountsAndSizeAcrossSubscriptions()
    {
        await using var harness = await IntegrationHarness.StartAsync();
        var admin = new ServiceBusAdministrationClient(harness.ConnectionString);
        await admin.CreateTopicAsync("rt-agg");
        await admin.CreateSubscriptionAsync("rt-agg", "s1");
        await admin.CreateSubscriptionAsync("rt-agg", "s2");

        await using var client = new ServiceBusClient(harness.ConnectionString);
        var sender = client.CreateSender("rt-agg");
        await sender.SendMessageAsync(new ServiceBusMessage("evt-1") { MessageId = "rt-1" });
        await sender.SendMessageAsync(new ServiceBusMessage("evt-2") { MessageId = "rt-2" });
        await sender.ScheduleMessageAsync(new ServiceBusMessage("later"), DateTimeOffset.UtcNow.AddHours(1));

        var receiver = client.CreateReceiver("rt-agg", "s1");
        var toDeadLetter = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
        toDeadLetter.ShouldNotBeNull();
        await receiver.DeadLetterMessageAsync(toDeadLetter, "testing", "topic aggregation");

        TopicRuntimeProperties runtime = await admin.GetTopicRuntimePropertiesAsync("rt-agg");
        runtime.SubscriptionCount.ShouldBe(2);
        runtime.ScheduledMessageCount.ShouldBe(1, "the scheduled publish is held at the topic until activation");
        runtime.SizeInBytes.ShouldBeGreaterThan(0);

        // The SDK's TopicRuntimeProperties surfaces only the scheduled count from
        // CountDetails, so the full aggregation is asserted on the wire shape directly:
        // s1 holds 1 active + 1 dead-lettered, s2 holds 2 active.
        var description = (await GetAtomXmlAsync(harness.Port, "rt-agg")).Descendants()
            .Single(e => e.Name.LocalName == "TopicDescription");
        CountDetail(description, "ActiveMessageCount").ShouldBe(3);
        CountDetail(description, "DeadLetterMessageCount").ShouldBe(1);
        CountDetail(description, "ScheduledMessageCount").ShouldBe(1);
        CountDetail(description, "TransferMessageCount").ShouldBe(0);
        CountDetail(description, "TransferDeadLetterMessageCount").ShouldBe(0);
    }

    [Fact]
    public async Task GetQueueRuntimePropertiesAsync_ReportsSizeInBytes()
    {
        await using var harness = await IntegrationHarness.StartAsync();
        var admin = new ServiceBusAdministrationClient(harness.ConnectionString);
        await admin.CreateQueueAsync("rt-sized");

        QueueRuntimeProperties before = await admin.GetQueueRuntimePropertiesAsync("rt-sized");
        before.SizeInBytes.ShouldBe(0);

        await using var client = new ServiceBusClient(harness.ConnectionString);
        await client.CreateSender("rt-sized").SendMessageAsync(new ServiceBusMessage("weigh me"));

        QueueRuntimeProperties after = await admin.GetQueueRuntimePropertiesAsync("rt-sized");
        after.SizeInBytes.ShouldBeGreaterThan(0);
        after.TotalMessageCount.ShouldBe(1);
    }

    [Fact]
    public async Task AccessedAt_MovesOnSendActivity_AndDivergesFromUpdatedAt()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
        await using var host = await OpenServiceBusTestHost.StartAsync(o => o.TimeProvider = clock);
        var admin = new ServiceBusAdministrationClient(host.ConnectionString);
        await admin.CreateQueueAsync("rt-touched");

        clock.Advance(TimeSpan.FromMinutes(10));
        await using var client = new ServiceBusClient(host.ConnectionString);
        await client.CreateSender("rt-touched").SendMessageAsync(new ServiceBusMessage("tick"));

        QueueRuntimeProperties runtime = await admin.GetQueueRuntimePropertiesAsync("rt-touched");
        runtime.AccessedAt.ShouldBe(clock.GetUtcNow(), "the send 10 fake minutes after creation is the last activity");
        runtime.AccessedAt.ShouldNotBe(runtime.UpdatedAt);
        runtime.AccessedAt.ShouldBeGreaterThan(runtime.CreatedAt);
    }

    [Fact]
    public async Task RuleCreatedAt_IsTheCreationInstant_AndSurvivesGetAndListRoundTrips()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
        await using var host = await OpenServiceBusTestHost.StartAsync(o => o.TimeProvider = clock);
        var admin = new ServiceBusAdministrationClient(host.ConnectionString);
        await admin.CreateTopicAsync("rt-rules");
        await admin.CreateSubscriptionAsync("rt-rules", "sub");

        clock.Advance(TimeSpan.FromMinutes(5));
        var expected = clock.GetUtcNow();
        await admin.CreateRuleAsync("rt-rules", "sub", new CreateRuleOptions("high", new SqlRuleFilter("priority > 1")));

        clock.Advance(TimeSpan.FromMinutes(7));
        // The .NET SDK's RuleProperties does not surface CreatedAt, so the wire value is
        // asserted on the ATOM XML directly - once from the single-rule GET, once from the list.
        var fetched = (await GetAtomXmlAsync(host.Port, "rt-rules/subscriptions/sub/rules/high")).Descendants()
            .Single(e => e.Name.LocalName == "RuleDescription");
        DateTimeOffset.Parse(CreatedAtOf(fetched)).ShouldBe(expected);

        var listed = (await GetAtomXmlAsync(host.Port, "rt-rules/subscriptions/sub/rules")).Descendants()
            .Single(e => e.Name.LocalName == "RuleDescription"
                && e.Elements().Any(c => c.Name.LocalName == "Name" && c.Value == "high"));
        DateTimeOffset.Parse(CreatedAtOf(listed)).ShouldBe(expected);

        static string CreatedAtOf(XElement ruleDescription) =>
            ruleDescription.Elements().Single(e => e.Name.LocalName == "CreatedAt").Value;
    }

    [Fact]
    public async Task ListingQueues_NeverMaterializesMessages()
    {
        PeekCountingStore? spy = null;
        await using var host = await OpenServiceBusTestHost.StartAsync(o =>
            o.StoreFactory = tp => spy = new PeekCountingStore(new InMemoryMessageStore(tp)));
        var admin = new ServiceBusAdministrationClient(host.ConnectionString);
        await using var client = new ServiceBusClient(host.ConnectionString);
        foreach (var name in new[] { "rt-list-a", "rt-list-b", "rt-list-c" })
        {
            await admin.CreateQueueAsync(name);
            var sender = client.CreateSender(name);
            await sender.SendMessageAsync(new ServiceBusMessage("backlog"));
            await sender.ScheduleMessageAsync(new ServiceBusMessage("later"), DateTimeOffset.UtcNow.AddHours(1));
            await sender.CloseAsync();
        }

        spy.ShouldNotBeNull();
        spy.PeekCalls = 0;
        var queues = new List<QueueRuntimeProperties>();
        await foreach (var queue in admin.GetQueuesRuntimePropertiesAsync())
        {
            queues.Add(queue);
        }

        queues.Count.ShouldBe(3);
        queues.Single(q => q.Name == "rt-list-a").ScheduledMessageCount.ShouldBe(1);
        queues.Single(q => q.Name == "rt-list-a").ActiveMessageCount.ShouldBe(1);
        spy.PeekCalls.ShouldBe(0, "runtime counters must come from count/size queries, not full peeks");
    }

    /// <summary>
    /// IMessageStore decorator that counts <see cref="Peek"/> calls and forwards everything
    /// (including the interface's default-implemented members, so the inner store's overrides
    /// stay in effect) to the real in-memory store.
    /// </summary>
    private sealed class PeekCountingStore : IMessageStore
    {
        private readonly IMessageStore _inner;

        public PeekCountingStore(IMessageStore inner) => _inner = inner;

        public int PeekCalls;

        public IReadOnlyList<StoredMessage> Peek(string queueName, long fromSequenceNumber, int maxCount)
        {
            Interlocked.Increment(ref PeekCalls);
            return _inner.Peek(queueName, fromSequenceNumber, maxCount);
        }

        public Task CreateQueueAsync(string queueName, CancellationToken ct = default) => _inner.CreateQueueAsync(queueName, ct);
        public Task DeleteQueueAsync(string queueName, CancellationToken ct = default) => _inner.DeleteQueueAsync(queueName, ct);
        public IReadOnlyCollection<string> ListQueueNames() => _inner.ListQueueNames();
        public Task SaveQueueDescriptorAsync(string queueName, string descriptorJson, CancellationToken ct = default) => _inner.SaveQueueDescriptorAsync(queueName, descriptorJson, ct);
        public IReadOnlyDictionary<string, string> LoadQueueDescriptors() => _inner.LoadQueueDescriptors();
        public Task SaveTopicDescriptorAsync(string topicName, string descriptorJson, CancellationToken ct = default) => _inner.SaveTopicDescriptorAsync(topicName, descriptorJson, ct);
        public Task DeleteTopicDescriptorAsync(string topicName, CancellationToken ct = default) => _inner.DeleteTopicDescriptorAsync(topicName, ct);
        public IReadOnlyDictionary<string, string> LoadTopicDescriptors() => _inner.LoadTopicDescriptors();
        public Task SaveSubscriptionDescriptorAsync(string topicName, string subscriptionName, string descriptorJson, CancellationToken ct = default) => _inner.SaveSubscriptionDescriptorAsync(topicName, subscriptionName, descriptorJson, ct);
        public Task DeleteSubscriptionDescriptorAsync(string topicName, string subscriptionName, CancellationToken ct = default) => _inner.DeleteSubscriptionDescriptorAsync(topicName, subscriptionName, ct);
        public IReadOnlyDictionary<string, string> LoadSubscriptionDescriptors() => _inner.LoadSubscriptionDescriptors();
        public Task SaveSubscriptionRuleAsync(string topicName, string subscriptionName, string ruleName, string ruleJson, CancellationToken ct = default) => _inner.SaveSubscriptionRuleAsync(topicName, subscriptionName, ruleName, ruleJson, ct);
        public Task DeleteSubscriptionRuleAsync(string topicName, string subscriptionName, string ruleName, CancellationToken ct = default) => _inner.DeleteSubscriptionRuleAsync(topicName, subscriptionName, ruleName, ct);
        public IReadOnlyDictionary<string, IReadOnlyList<string>> LoadSubscriptionRules() => _inner.LoadSubscriptionRules();
        public Task<bool> CheckTopicDuplicateAsync(string topicName, string messageId, TimeSpan window, CancellationToken ct = default) => _inner.CheckTopicDuplicateAsync(topicName, messageId, window, ct);
        public Task ClearTopicDedupHistoryAsync(string topicName, CancellationToken ct = default) => _inner.ClearTopicDedupHistoryAsync(topicName, ct);
        public Task<StoredMessage> EnqueueAsync(string queueName, byte[] encodedMessage, DateTimeOffset? expiresAt = null, DateTimeOffset? scheduledEnqueueTime = null, string? sessionId = null, string? messageId = null, TimeSpan? duplicateDetectionWindow = null, int deliveryCount = 0, long? enqueuedSequenceNumber = null, CancellationToken ct = default) => _inner.EnqueueAsync(queueName, encodedMessage, expiresAt, scheduledEnqueueTime, sessionId, messageId, duplicateDetectionWindow, deliveryCount, enqueuedSequenceNumber, ct);
        public Task<long> AllocateSequenceNumberAsync(string entityName, CancellationToken ct = default) => _inner.AllocateSequenceNumberAsync(entityName, ct);
        public int ActivateScheduled(string queueName, DateTimeOffset now) => _inner.ActivateScheduled(queueName, now);
        public Task<bool> TryCancelScheduledAsync(string queueName, long sequenceNumber, CancellationToken ct = default) => _inner.TryCancelScheduledAsync(queueName, sequenceNumber, ct);
        public IReadOnlyList<StoredMessage> ExpireMessages(string queueName, DateTimeOffset now) => _inner.ExpireMessages(queueName, now);
        public Task<long> CountAsync(string queueName, CancellationToken ct = default) => _inner.CountAsync(queueName, ct);
        public long CountScheduled(string queueName) => _inner.CountScheduled(queueName);
        public long GetSizeInBytes(string queueName) => _inner.GetSizeInBytes(queueName);
        public (long Enqueued, long Completed) LifetimeCounters(string queueName) => _inner.LifetimeCounters(queueName);
        public Task<long> PurgeAsync(string queueName, CancellationToken ct = default) => _inner.PurgeAsync(queueName, ct);
        public Task<LockedMessage?> TryDequeueAsync(string queueName, TimeSpan lockDuration, string? associatedLinkName = null, CancellationToken ct = default) => _inner.TryDequeueAsync(queueName, lockDuration, associatedLinkName, ct);
        public Task<bool> TryCompleteAsync(string queueName, Guid lockToken, CancellationToken ct = default) => _inner.TryCompleteAsync(queueName, lockToken, ct);
        public Task<bool> TryAbandonAsync(string queueName, Guid lockToken, CancellationToken ct = default) => _inner.TryAbandonAsync(queueName, lockToken, ct);
        public int ExpireLocks(string queueName, DateTimeOffset now) => _inner.ExpireLocks(queueName, now);
        public Task<DateTimeOffset?> TryRenewLockAsync(string queueName, Guid lockToken, TimeSpan lockDuration, string? requestingLinkName = null, CancellationToken ct = default) => _inner.TryRenewLockAsync(queueName, lockToken, lockDuration, requestingLinkName, ct);
        public Task<StoredMessage?> TryRemoveLockedAsync(string queueName, Guid lockToken, CancellationToken ct = default) => _inner.TryRemoveLockedAsync(queueName, lockToken, ct);
        public Task<bool> TryDeferAsync(string queueName, Guid lockToken, CancellationToken ct = default) => _inner.TryDeferAsync(queueName, lockToken, ct);
        public Task<StoredMessage?> TryGetLockedAsync(string queueName, Guid lockToken, CancellationToken ct = default) => _inner.TryGetLockedAsync(queueName, lockToken, ct);
        public Task<bool> TryUpdateLockedPayloadAsync(string queueName, Guid lockToken, byte[] encodedMessage, CancellationToken ct = default) => _inner.TryUpdateLockedPayloadAsync(queueName, lockToken, encodedMessage, ct);
        public Task<LockedMessage?> TryReceiveDeferredAsync(string queueName, long sequenceNumber, TimeSpan lockDuration, string? associatedLinkName = null, CancellationToken ct = default) => _inner.TryReceiveDeferredAsync(queueName, sequenceNumber, lockDuration, associatedLinkName, ct);
        public Task<SessionLock?> TryAcceptSessionAsync(string queueName, string sessionId, TimeSpan lockDuration, string? linkName = null, CancellationToken ct = default) => _inner.TryAcceptSessionAsync(queueName, sessionId, lockDuration, linkName, ct);
        public Task<SessionLock?> TryAcceptNextSessionAsync(string queueName, TimeSpan lockDuration, string? linkName = null, CancellationToken ct = default) => _inner.TryAcceptNextSessionAsync(queueName, lockDuration, linkName, ct);
        public Task<LockedMessage?> TryDequeueFromSessionAsync(string queueName, string sessionId, TimeSpan messageLockDuration, string? linkName = null, CancellationToken ct = default) => _inner.TryDequeueFromSessionAsync(queueName, sessionId, messageLockDuration, linkName, ct);
        public Task<DateTimeOffset?> TryRenewSessionLockAsync(string queueName, string sessionId, TimeSpan lockDuration, string? requestingLinkName = null, CancellationToken ct = default) => _inner.TryRenewSessionLockAsync(queueName, sessionId, lockDuration, requestingLinkName, ct);
        public Task ReleaseSessionAsync(string queueName, string sessionId, string? expectedLinkName = null, CancellationToken ct = default) => _inner.ReleaseSessionAsync(queueName, sessionId, expectedLinkName, ct);
        public Task SetSessionStateAsync(string queueName, string sessionId, byte[]? state, CancellationToken ct = default) => _inner.SetSessionStateAsync(queueName, sessionId, state, ct);
        public Task<byte[]?> GetSessionStateAsync(string queueName, string sessionId, CancellationToken ct = default) => _inner.GetSessionStateAsync(queueName, sessionId, ct);
        public Task<bool> IsSessionLockHeldAsync(string queueName, string sessionId, string? linkName = null, CancellationToken ct = default) => _inner.IsSessionLockHeldAsync(queueName, sessionId, linkName, ct);
        public IReadOnlyList<string> ListSessions(string queueName, DateTimeOffset? stateUpdatedAfter = null, int skip = 0, int top = int.MaxValue) => _inner.ListSessions(queueName, stateUpdatedAfter, skip, top);
    }
}
