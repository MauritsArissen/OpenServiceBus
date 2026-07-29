using Microsoft.Extensions.Logging.Abstractions;
using OpenServiceBus.Core.Entities;
using OpenServiceBus.Core.Filters;
using OpenServiceBus.InMemoryStorage.Queues;
using OpenServiceBus.InMemoryStorage.Routing;
using OpenServiceBus.InMemoryStorage.Topics;

namespace OpenServiceBus.InMemoryStorage.Tests;

/// <summary>
/// Session routing through topic fan-out (issue #18). Sessions are a per-subscription
/// concern in Service Bus: the topic just carries the message's group-id, and each matching
/// subscription decides how to store its copy - per-session channels when it requires
/// sessions, the plain channel when it doesn't, its DLQ when it requires sessions but the
/// message has no session id.
/// </summary>
public class SessionTopicRoutingTests
{
    private static readonly byte[] Payload = [0x01, 0x02, 0x03];

    private static (MessageRouter Router, TopicManager Topics, QueueManager Queues, InMemoryMessageStore Store) NewFixture()
    {
        var store = new InMemoryMessageStore();
        var queues = new QueueManager(store);
        var topics = new TopicManager(queues);
        var router = new MessageRouter(queues, store, NullLogger<MessageRouter>.Instance, topics);
        return (router, topics, queues, store);
    }

    private static MessageFilterContext Msg(string? sessionId = null) => new()
    {
        SessionId = sessionId,
        ApplicationProperties = new Dictionary<string, object?>(),
        EnqueuedTimeUtc = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task RouteAsync_SessionMessageToSessionSubscription_LandsInThePerSessionChannel()
    {
        // Arrange
        var (router, topics, _, store) = NewFixture();
        await topics.CreateTopicAsync(new TopicDescriptor { Name = "events" });
        var sub = await topics.CreateSubscriptionAsync(new SubscriptionDescriptor
        {
            TopicName = "events",
            Name = "sessions",
            RequiresSession = true,
        });

        // Act - topic senders don't thread an explicit session id; the group-id rides in the
        // filter context, exactly like TopicSenderProcessor builds it.
        var landed = await router.RouteAsync("events", Payload, filterContext: Msg(sessionId: "s-1"));

        // Assert - the copy is claimable through the session machinery on the backing queue.
        landed.ShouldBe(new[] { sub.BackingQueueName });
        var sessionLock = await store.TryAcceptSessionAsync(sub.BackingQueueName, "s-1", TimeSpan.FromMinutes(1));
        sessionLock.ShouldNotBeNull("the fanned-out copy must create a claimable session");
        var locked = await store.TryDequeueFromSessionAsync(sub.BackingQueueName, "s-1", TimeSpan.FromMinutes(1));
        locked.ShouldNotBeNull();
        locked.Message.SessionId.ShouldBe("s-1");
    }

    [Fact]
    public async Task RouteAsync_SessionMessage_PlainSiblingSubscriptionStillGetsAReadableCopy()
    {
        // Arrange - one topic, one session-enabled subscription and one plain one.
        var (router, topics, _, store) = NewFixture();
        await topics.CreateTopicAsync(new TopicDescriptor { Name = "events" });
        await topics.CreateSubscriptionAsync(new SubscriptionDescriptor
        {
            TopicName = "events",
            Name = "sessions",
            RequiresSession = true,
        });
        var plain = await topics.CreateSubscriptionAsync(new SubscriptionDescriptor
        {
            TopicName = "events",
            Name = "plain",
        });

        // Act
        var landed = await router.RouteAsync("events", Payload, filterContext: Msg(sessionId: "s-1"));

        // Assert - the plain subscription's copy must NOT be hidden in a session channel;
        // an ordinary dequeue (what its receivers do) has to see it.
        landed.Count.ShouldBe(2);
        var locked = await store.TryDequeueAsync(plain.BackingQueueName, TimeSpan.FromMinutes(1));
        locked.ShouldNotBeNull("a sessionless subscription treats session messages as normal messages");
    }

    [Fact]
    public async Task RouteAsync_SessionlessMessageToSessionSubscription_CopyIsDeadLettered()
    {
        // Arrange
        var (router, topics, _, store) = NewFixture();
        await topics.CreateTopicAsync(new TopicDescriptor { Name = "events" });
        var sessionSub = await topics.CreateSubscriptionAsync(new SubscriptionDescriptor
        {
            TopicName = "events",
            Name = "sessions",
            RequiresSession = true,
        });
        var plain = await topics.CreateSubscriptionAsync(new SubscriptionDescriptor
        {
            TopicName = "events",
            Name = "plain",
        });

        // Act - no session id anywhere on the message.
        var landed = await router.RouteAsync("events", Payload, filterContext: Msg(sessionId: null));

        // Assert - the session subscription can never deliver a sessionless message, so its
        // copy goes to its DLQ (like Azure) instead of being stranded invisibly; the plain
        // sibling is unaffected.
        landed.ShouldContain(sessionSub.BackingQueueName + "/$DeadLetterQueue");
        landed.ShouldContain(plain.BackingQueueName);
        (await store.CountAsync(sessionSub.BackingQueueName)).ShouldBe(0);
        (await store.CountAsync(sessionSub.BackingQueueName + "/$DeadLetterQueue")).ShouldBe(1);
    }

    [Fact]
    public async Task RouteAsync_ExplicitSessionIdParameter_StillWinsOverFilterContext()
    {
        // Arrange - callers that DO thread a session id (queue ForwardTo hops) keep priority.
        var (router, topics, _, store) = NewFixture();
        await topics.CreateTopicAsync(new TopicDescriptor { Name = "events" });
        var sub = await topics.CreateSubscriptionAsync(new SubscriptionDescriptor
        {
            TopicName = "events",
            Name = "sessions",
            RequiresSession = true,
        });

        // Act
        await router.RouteAsync("events", Payload, sessionId: "explicit", filterContext: Msg(sessionId: "from-context"));

        // Assert
        var sessionLock = await store.TryAcceptSessionAsync(sub.BackingQueueName, "explicit", TimeSpan.FromMinutes(1));
        sessionLock.ShouldNotBeNull("the explicitly threaded session id takes precedence");
    }
}
