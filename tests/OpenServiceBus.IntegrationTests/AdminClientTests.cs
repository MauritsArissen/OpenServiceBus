using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;

namespace OpenServiceBus.IntegrationTests;

/// <summary>
/// The real <c>ServiceBusAdministrationClient</c> against the broker's ATOM management API,
/// over the SAME connection string the data-plane <c>ServiceBusClient</c> uses. The SDK derives
/// its HTTP endpoint from the connection string's single port, so these tests also exercise the
/// protocol front door that serves AMQP and HTTP on one socket.
/// </summary>
public class AdminClientTests
{
    [Fact]
    public async Task CreateQueueAsync_RoundTripsEnforcedProperties()
    {
        await using var harness = await IntegrationHarness.StartAsync();
        var admin = new ServiceBusAdministrationClient(harness.ConnectionString);

        var created = await admin.CreateQueueAsync(new CreateQueueOptions("admin-orders")
        {
            MaxDeliveryCount = 4,
            LockDuration = TimeSpan.FromSeconds(42),
            RequiresSession = true,
            DeadLetteringOnMessageExpiration = true,
            DefaultMessageTimeToLive = TimeSpan.FromMinutes(5),
            UserMetadata = "made-by-admin-client",
        });

        created.Value.Name.ShouldBe("admin-orders");
        created.Value.MaxDeliveryCount.ShouldBe(4);

        QueueProperties fetched = await admin.GetQueueAsync("admin-orders");
        fetched.MaxDeliveryCount.ShouldBe(4);
        fetched.LockDuration.ShouldBe(TimeSpan.FromSeconds(42));
        fetched.RequiresSession.ShouldBeTrue();
        fetched.DeadLetteringOnMessageExpiration.ShouldBeTrue();
        fetched.DefaultMessageTimeToLive.ShouldBe(TimeSpan.FromMinutes(5));
        fetched.UserMetadata.ShouldBe("made-by-admin-client");

        // The broker registry sees exactly what the SDK created.
        var descriptor = await harness.Queues.GetAsync("admin-orders");
        descriptor.ShouldNotBeNull();
        descriptor.MaxDeliveryCount.ShouldBe(4);
        descriptor.LockDuration.ShouldBe(TimeSpan.FromSeconds(42));
        descriptor.RequiresSession.ShouldBeTrue();
    }

    [Fact]
    public async Task CreateQueueAsync_ThenSendAndReceive_OverTheSameConnectionString()
    {
        await using var harness = await IntegrationHarness.StartAsync();
        var admin = new ServiceBusAdministrationClient(harness.ConnectionString);
        await admin.CreateQueueAsync("provisioned-by-sdk");

        await using var client = new ServiceBusClient(harness.ConnectionString);
        var sender = client.CreateSender("provisioned-by-sdk");
        await sender.SendMessageAsync(new ServiceBusMessage("through the front door") { MessageId = "fd-1" });

        var receiver = client.CreateReceiver("provisioned-by-sdk");
        var received = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));

        received.ShouldNotBeNull();
        received.MessageId.ShouldBe("fd-1");
        received.Body.ToString().ShouldBe("through the front door");
        await receiver.CompleteMessageAsync(received);
    }

    [Fact]
    public async Task CreateQueueAsync_WhenItAlreadyExists_ThrowsEntityAlreadyExists()
    {
        await using var harness = await IntegrationHarness.StartAsync();
        var admin = new ServiceBusAdministrationClient(harness.ConnectionString);
        await admin.CreateQueueAsync("dup-queue");

        var ex = await Should.ThrowAsync<ServiceBusException>(() => admin.CreateQueueAsync("dup-queue"));
        ex.Reason.ShouldBe(ServiceBusFailureReason.MessagingEntityAlreadyExists);
    }

    [Fact]
    public async Task GetQueueAsync_WhenMissing_ThrowsEntityNotFound_AndExistsReturnsFalse()
    {
        await using var harness = await IntegrationHarness.StartAsync();
        var admin = new ServiceBusAdministrationClient(harness.ConnectionString);

        var ex = await Should.ThrowAsync<ServiceBusException>(() => admin.GetQueueAsync("nope"));
        ex.Reason.ShouldBe(ServiceBusFailureReason.MessagingEntityNotFound);
        (await admin.QueueExistsAsync("nope")).Value.ShouldBeFalse();
    }

    [Fact]
    public async Task DeleteQueueAsync_RemovesTheQueue()
    {
        await using var harness = await IntegrationHarness.StartAsync();
        var admin = new ServiceBusAdministrationClient(harness.ConnectionString);
        await admin.CreateQueueAsync("short-lived");
        (await admin.QueueExistsAsync("short-lived")).Value.ShouldBeTrue();

        await admin.DeleteQueueAsync("short-lived");

        (await admin.QueueExistsAsync("short-lived")).Value.ShouldBeFalse();
        (await harness.Queues.GetAsync("short-lived")).ShouldBeNull();
    }

    [Fact]
    public async Task UpdateQueueAsync_ChangesMutableProperties()
    {
        await using var harness = await IntegrationHarness.StartAsync();
        var admin = new ServiceBusAdministrationClient(harness.ConnectionString);
        await admin.CreateQueueAsync("tunable");

        QueueProperties current = await admin.GetQueueAsync("tunable");
        current.MaxDeliveryCount = 3;
        current.LockDuration = TimeSpan.FromSeconds(15);
        QueueProperties updated = await admin.UpdateQueueAsync(current);

        updated.MaxDeliveryCount.ShouldBe(3);
        updated.LockDuration.ShouldBe(TimeSpan.FromSeconds(15));

        // The registry - and therefore actual delivery behaviour - reflects the update.
        var descriptor = await harness.Queues.GetAsync("tunable");
        descriptor.ShouldNotBeNull();
        descriptor.MaxDeliveryCount.ShouldBe(3);
        descriptor.LockDuration.ShouldBe(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task GetQueuesAsync_ListsQueues_WithoutInternalEntities()
    {
        await using var harness = await IntegrationHarness.StartAsync();
        var admin = new ServiceBusAdministrationClient(harness.ConnectionString);
        await admin.CreateQueueAsync("list-a");
        await admin.CreateQueueAsync("list-b");
        await admin.CreateTopicAsync("list-topic");
        await admin.CreateSubscriptionAsync("list-topic", "list-sub");

        var names = new List<string>();
        await foreach (var queue in admin.GetQueuesAsync())
        {
            names.Add(queue.Name);
        }

        names.ShouldContain("list-a");
        names.ShouldContain("list-b");
        // Dead-letter sub-entities and subscription backing queues are implementation details.
        names.ShouldAllBe(n => !n.Contains('$'));
        names.ShouldAllBe(n => !n.Contains("Subscriptions", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GetQueueRuntimePropertiesAsync_ReportsPerStateCounts()
    {
        await using var harness = await IntegrationHarness.StartAsync();
        var admin = new ServiceBusAdministrationClient(harness.ConnectionString);
        await admin.CreateQueueAsync("counted");

        await using var client = new ServiceBusClient(harness.ConnectionString);
        var sender = client.CreateSender("counted");
        await sender.SendMessageAsync(new ServiceBusMessage("active-1"));
        await sender.SendMessageAsync(new ServiceBusMessage("active-2"));
        await sender.ScheduleMessageAsync(new ServiceBusMessage("later"), DateTimeOffset.UtcNow.AddHours(1));

        var receiver = client.CreateReceiver("counted");
        var toDeadLetter = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
        await receiver.DeadLetterMessageAsync(toDeadLetter, "testing", "runtime counters");

        QueueRuntimeProperties runtime = await admin.GetQueueRuntimePropertiesAsync("counted");
        runtime.ActiveMessageCount.ShouldBe(1);
        runtime.ScheduledMessageCount.ShouldBe(1);
        runtime.DeadLetterMessageCount.ShouldBe(1);
        runtime.TotalMessageCount.ShouldBe(3);
    }

    [Fact]
    public async Task CreateTopicAndSubscription_RoundTrip_AndFanOutWorks()
    {
        await using var harness = await IntegrationHarness.StartAsync();
        var admin = new ServiceBusAdministrationClient(harness.ConnectionString);

        await admin.CreateTopicAsync(new CreateTopicOptions("admin-events"));
        await admin.CreateSubscriptionAsync(new CreateSubscriptionOptions("admin-events", "audit")
        {
            MaxDeliveryCount = 7,
            LockDuration = TimeSpan.FromSeconds(30),
        });

        SubscriptionProperties sub = await admin.GetSubscriptionAsync("admin-events", "audit");
        sub.SubscriptionName.ShouldBe("audit");
        sub.MaxDeliveryCount.ShouldBe(7);

        (await admin.TopicExistsAsync("admin-events")).Value.ShouldBeTrue();
        (await admin.SubscriptionExistsAsync("admin-events", "audit")).Value.ShouldBeTrue();

        // Publish through the topic and receive from the admin-created subscription.
        await using var client = new ServiceBusClient(harness.ConnectionString);
        await client.CreateSender("admin-events").SendMessageAsync(new ServiceBusMessage("fan-out") { MessageId = "t-1" });
        var receiver = client.CreateReceiver("admin-events", "audit");
        var received = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
        received.ShouldNotBeNull();
        received.MessageId.ShouldBe("t-1");
        await receiver.CompleteMessageAsync(received);
    }

    [Fact]
    public async Task CreateSubscriptionAsync_WithSqlRule_ReplacesDefaultAndFilters()
    {
        await using var harness = await IntegrationHarness.StartAsync();
        var admin = new ServiceBusAdministrationClient(harness.ConnectionString);
        await admin.CreateTopicAsync("filtered-events");

        await admin.CreateSubscriptionAsync(
            new CreateSubscriptionOptions("filtered-events", "high-only"),
            new CreateRuleOptions("high", new SqlRuleFilter("priority > 5")));

        var rules = new List<RuleProperties>();
        await foreach (var rule in admin.GetRulesAsync("filtered-events", "high-only"))
        {
            rules.Add(rule);
        }
        rules.Count.ShouldBe(1);
        rules[0].Name.ShouldBe("high");
        rules[0].Filter.ShouldBeOfType<SqlRuleFilter>().SqlExpression.ShouldBe("priority > 5");

        // Filtering is live: only the matching message reaches the subscription.
        await using var client = new ServiceBusClient(harness.ConnectionString);
        var sender = client.CreateSender("filtered-events");
        var low = new ServiceBusMessage("low") { MessageId = "low-1" };
        low.ApplicationProperties["priority"] = 1;
        var high = new ServiceBusMessage("high") { MessageId = "high-1" };
        high.ApplicationProperties["priority"] = 9;
        await sender.SendMessageAsync(low);
        await sender.SendMessageAsync(high);

        var receiver = client.CreateReceiver("filtered-events", "high-only");
        var received = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
        received.ShouldNotBeNull();
        received.MessageId.ShouldBe("high-1");
        await receiver.CompleteMessageAsync(received);
        (await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(2))).ShouldBeNull();
    }

    [Fact]
    public async Task CreateRuleAsync_CorrelationFilter_RoundTripsAndDeletes()
    {
        await using var harness = await IntegrationHarness.StartAsync();
        var admin = new ServiceBusAdministrationClient(harness.ConnectionString);
        await admin.CreateTopicAsync("corr-events");
        await admin.CreateSubscriptionAsync("corr-events", "by-subject");

        var filter = new CorrelationRuleFilter { Subject = "invoice", CorrelationId = "corr-42" };
        filter.ApplicationProperties["region"] = "eu";
        await admin.CreateRuleAsync("corr-events", "by-subject", new CreateRuleOptions("corr", filter));
        await admin.DeleteRuleAsync("corr-events", "by-subject", "$Default");

        RuleProperties fetched = await admin.GetRuleAsync("corr-events", "by-subject", "corr");
        var roundTripped = fetched.Filter.ShouldBeOfType<CorrelationRuleFilter>();
        roundTripped.Subject.ShouldBe("invoice");
        roundTripped.CorrelationId.ShouldBe("corr-42");
        roundTripped.ApplicationProperties["region"].ShouldBe("eu");

        await admin.DeleteRuleAsync("corr-events", "by-subject", "corr");
        var rules = new List<RuleProperties>();
        await foreach (var rule in admin.GetRulesAsync("corr-events", "by-subject"))
        {
            rules.Add(rule);
        }
        rules.ShouldBeEmpty();
    }

    [Fact]
    public async Task DeleteTopicAsync_RemovesTopicAndSubscriptions()
    {
        await using var harness = await IntegrationHarness.StartAsync();
        var admin = new ServiceBusAdministrationClient(harness.ConnectionString);
        await admin.CreateTopicAsync("doomed-topic");
        await admin.CreateSubscriptionAsync("doomed-topic", "doomed-sub");

        await admin.DeleteTopicAsync("doomed-topic");

        (await admin.TopicExistsAsync("doomed-topic")).Value.ShouldBeFalse();
        (await harness.Topics.GetSubscriptionAsync("doomed-topic", "doomed-sub")).ShouldBeNull();
    }

    [Fact]
    public async Task GetNamespacePropertiesAsync_ReturnsNamespaceInfo()
    {
        await using var harness = await IntegrationHarness.StartAsync();
        var admin = new ServiceBusAdministrationClient(harness.ConnectionString);

        NamespaceProperties ns = await admin.GetNamespacePropertiesAsync();

        ns.Name.ShouldBe("127.0.0.1");
        ns.MessagingSku.ShouldBe(MessagingSku.Standard);
    }

    [Fact]
    public async Task AdminClient_WithSasAuthRequired_AcceptsValidKeyAndRejectsWrongKey()
    {
        await using var harness = await IntegrationHarness.StartAsync(o =>
        {
            o.RequireSasAuth = true;
            o.SasKeys["RootManageSharedAccessKey"] = "SAS_KEY_VALUE";
        });

        var admin = new ServiceBusAdministrationClient(harness.ConnectionString);
        await admin.CreateQueueAsync("authed-queue");
        (await admin.QueueExistsAsync("authed-queue")).Value.ShouldBeTrue();

        var badConnectionString = harness.ConnectionString.Replace(
            "SharedAccessKey=SAS_KEY_VALUE", "SharedAccessKey=WRONG_KEY", StringComparison.Ordinal);
        var badAdmin = new ServiceBusAdministrationClient(badConnectionString);
        await Should.ThrowAsync<UnauthorizedAccessException>(() => badAdmin.CreateQueueAsync("should-not-exist"));
    }
}
