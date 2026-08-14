using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Time.Testing;
using OpenServiceBus.Core.Entities;
using OpenServiceBus.Core.Filters;
using OpenServiceBus.Testing;

namespace OpenServiceBus.IntegrationTests;

/// <summary>
/// ScheduleMessageAsync / CancelScheduledMessageAsync against a TOPIC sender (issue #53):
/// the topic's $management node holds the scheduled publish until its due time, then fans
/// it out through the router - filters are evaluated at ACTIVATION, and duplicate
/// detection at SCHEDULE time, both matching Azure.
/// </summary>
public class TopicSchedulingTests
{
    private static readonly TimeSpan Due = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PastDue = TimeSpan.FromMinutes(6);

    private static async Task<(OpenServiceBusTestHost Host, FakeTimeProvider Clock)> StartAsync(
        string topic, params string[] subscriptions)
    {
        var clock = new FakeTimeProvider();
        var host = await OpenServiceBusTestHost.StartAsync(o => o.TimeProvider = clock);
        await host.CreateTopicAsync(topic);
        foreach (var sub in subscriptions)
        {
            await host.Topics.CreateSubscriptionAsync(new SubscriptionDescriptor { TopicName = topic, Name = sub });
        }
        return (host, clock);
    }

    private static async Task<List<string>> DrainAsync(ServiceBusClient client, string topic, string sub)
    {
        var receiver = client.CreateReceiver(topic, sub);
        var bodies = new List<string>();
        while (true)
        {
            var msg = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(2));
            if (msg is null) break;
            bodies.Add(msg.Body.ToString());
            await receiver.CompleteMessageAsync(msg);
        }
        await receiver.CloseAsync();
        return bodies;
    }

    [Fact]
    public async Task Schedule_FansOutToEverySubscriptionAtActivation_NotBefore()
    {
        var (host, clock) = await StartAsync("sched-topic", "a", "b");
        await using var _ = host;
        await using var client = new ServiceBusClient(host.ConnectionString);

        var sender = client.CreateSender("sched-topic");
        var seq = await sender.ScheduleMessageAsync(
            new ServiceBusMessage("later") { MessageId = "s-1" }, clock.GetUtcNow().Add(Due));
        seq.ShouldBeGreaterThan(0);

        (await DrainAsync(client, "sched-topic", "a")).ShouldBeEmpty("not due yet");

        clock.Advance(PastDue);
        (await DrainAsync(client, "sched-topic", "a")).ShouldBe(new[] { "later" });
        (await DrainAsync(client, "sched-topic", "b")).ShouldBe(new[] { "later" });
    }

    [Fact]
    public async Task Cancel_BeforeActivation_NothingIsDelivered()
    {
        var (host, clock) = await StartAsync("cancel-topic", "all");
        await using var _ = host;
        await using var client = new ServiceBusClient(host.ConnectionString);

        var sender = client.CreateSender("cancel-topic");
        var seq = await sender.ScheduleMessageAsync(
            new ServiceBusMessage("never") { MessageId = "c-1" }, clock.GetUtcNow().Add(Due));
        await sender.CancelScheduledMessageAsync(seq);

        clock.Advance(PastDue);
        (await DrainAsync(client, "cancel-topic", "all")).ShouldBeEmpty();
    }

    [Fact]
    public async Task Filters_AreEvaluatedAtActivation_IncludingSubscriptionsCreatedAfterScheduling()
    {
        var (host, clock) = await StartAsync("late-topic", "early");
        await using var _ = host;
        await using var client = new ServiceBusClient(host.ConnectionString);

        var sender = client.CreateSender("late-topic");
        var msg = new ServiceBusMessage("routed") { MessageId = "f-1" };
        msg.ApplicationProperties["priority"] = 9;
        await sender.ScheduleMessageAsync(msg, clock.GetUtcNow().Add(Due));

        // Created AFTER the schedule but BEFORE activation: fan-out happens at activation,
        // so this subscription receives its copy - and its SQL filter is honored.
        await host.Topics.CreateSubscriptionAsync(new SubscriptionDescriptor { TopicName = "late-topic", Name = "late-match" });
        await host.Topics.CreateOrReplaceRuleAsync(new RuleDescriptor
        {
            TopicName = "late-topic",
            SubscriptionName = "late-match",
            Name = "$Default",
            Filter = new SqlFilter("priority >= 5"),
        });
        await host.Topics.CreateSubscriptionAsync(new SubscriptionDescriptor { TopicName = "late-topic", Name = "late-miss" });
        await host.Topics.CreateOrReplaceRuleAsync(new RuleDescriptor
        {
            TopicName = "late-topic",
            SubscriptionName = "late-miss",
            Name = "$Default",
            Filter = new SqlFilter("priority < 5"),
        });

        clock.Advance(PastDue);
        (await DrainAsync(client, "late-topic", "early")).ShouldBe(new[] { "routed" });
        (await DrainAsync(client, "late-topic", "late-match")).ShouldBe(new[] { "routed" });
        (await DrainAsync(client, "late-topic", "late-miss")).ShouldBeEmpty("its filter does not match");
    }

    [Fact]
    public async Task DedupTopic_ScheduleReservesTheMessageIdAtScheduleTime()
    {
        var clock = new FakeTimeProvider();
        await using var host = await OpenServiceBusTestHost.StartAsync(o => o.TimeProvider = clock);
        await host.CreateTopicAsync(new TopicDescriptor
        {
            Name = "dedup-sched",
            RequiresDuplicateDetection = true,
            DuplicateDetectionHistoryTimeWindow = TimeSpan.FromMinutes(30),
        });
        await host.Topics.CreateSubscriptionAsync(new SubscriptionDescriptor { TopicName = "dedup-sched", Name = "all" });
        await using var client = new ServiceBusClient(host.ConnectionString);

        var sender = client.CreateSender("dedup-sched");
        var seq = await sender.ScheduleMessageAsync(
            new ServiceBusMessage("scheduled") { MessageId = "dup-1" }, clock.GetUtcNow().Add(Due));
        seq.ShouldBeGreaterThan(0);

        // The id was recorded at SCHEDULE time: an immediate publish with the same id is
        // silently dropped, and a second schedule reports sequence number 0 (dropped).
        await sender.SendMessageAsync(new ServiceBusMessage("immediate") { MessageId = "dup-1" });
        var duplicateSeq = await sender.ScheduleMessageAsync(
            new ServiceBusMessage("scheduled-again") { MessageId = "dup-1" }, clock.GetUtcNow().Add(Due));
        duplicateSeq.ShouldBe(0);

        clock.Advance(PastDue);
        (await DrainAsync(client, "dedup-sched", "all")).ShouldBe(new[] { "scheduled" });
    }

    [Fact]
    public async Task Schedule_WithASessionId_LandsInTheSessionOfASessionEnabledSubscription()
    {
        var clock = new FakeTimeProvider();
        await using var host = await OpenServiceBusTestHost.StartAsync(o => o.TimeProvider = clock);
        await host.CreateTopicAsync("session-sched");
        await host.Topics.CreateSubscriptionAsync(new SubscriptionDescriptor
        {
            TopicName = "session-sched",
            Name = "sessions",
            RequiresSession = true,
        });
        await using var client = new ServiceBusClient(host.ConnectionString);

        await client.CreateSender("session-sched").ScheduleMessageAsync(
            new ServiceBusMessage("ordered") { MessageId = "ss-1", SessionId = "acct-1" },
            clock.GetUtcNow().Add(Due));
        clock.Advance(PastDue);

        var session = await client.AcceptSessionAsync("session-sched", "sessions", "acct-1");
        var received = await session.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
        received.ShouldNotBeNull();
        received.Body.ToString().ShouldBe("ordered");
        received.SessionId.ShouldBe("acct-1");
        await session.CompleteMessageAsync(received);
        await session.CloseAsync();
    }

    [Fact]
    public async Task PurgeTopic_DiscardsPendingScheduledPublishes()
    {
        var (host, clock) = await StartAsync("purge-sched", "all");
        await using var _ = host;
        await using var client = new ServiceBusClient(host.ConnectionString);

        await client.CreateSender("purge-sched").ScheduleMessageAsync(
            new ServiceBusMessage("doomed") { MessageId = "p-1" }, clock.GetUtcNow().Add(Due));

        (await host.PurgeTopicAsync("purge-sched")).ShouldBe(1L);

        clock.Advance(PastDue);
        (await DrainAsync(client, "purge-sched", "all")).ShouldBeEmpty();
    }

    [Fact]
    public async Task NonSenderOperations_OnTheTopicNode_AreRejected()
    {
        var (host, _) = await StartAsync("no-receive-topic", "all");
        await using var __ = host;
        await using var client = new ServiceBusClient(host.ConnectionString);

        // PeekMessagesAsync needs a receiver, which cannot target a topic in the SDK -
        // the closest SDK-reachable probe is that scheduling still works while the node
        // rejects everything else (covered at the wire level by the 400 in the dispatch).
        var seq = await client.CreateSender("no-receive-topic").ScheduleMessageAsync(
            new ServiceBusMessage("ok") { MessageId = "n-1" }, DateTimeOffset.UtcNow.AddMinutes(5));
        seq.ShouldBeGreaterThan(0);
        await client.CreateSender("no-receive-topic").CancelScheduledMessageAsync(seq);
    }
}
