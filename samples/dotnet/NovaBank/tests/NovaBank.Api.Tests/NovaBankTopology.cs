using NovaBank.Api.Configuration;
using OpenServiceBus.Core.Entities;
using OpenServiceBus.Core.Filters;
using OpenServiceBus.Testing;

namespace NovaBank.Api.Tests;

/// <summary>
/// Creates the same queues / topic / subscriptions / rules as tmp/servicebus-config.json,
/// but programmatically against an embedded OpenServiceBusTestHost. Entity names come from
/// the app's own <see cref="ServiceBusOptions"/> defaults so the two can never drift apart.
/// </summary>
public static class NovaBankTopology
{
    public static async Task CreateAsync(OpenServiceBusTestHost bus)
    {
        var names = new ServiceBusOptions();

        await bus.Queues.CreateAsync(new QueueDescriptor
        {
            Name = names.TransfersQueue,
            LockDuration = TimeSpan.FromSeconds(30),
            MaxDeliveryCount = 3,
            RequiresDuplicateDetection = true,
            DuplicateDetectionHistoryTimeWindow = TimeSpan.FromMinutes(10),
        });

        await bus.Queues.CreateAsync(new QueueDescriptor
        {
            Name = names.PaymentsQueue,
            LockDuration = TimeSpan.FromSeconds(30),
            MaxDeliveryCount = 5,
            RequiresSession = true,
        });

        await bus.Topics.CreateTopicAsync(new TopicDescriptor { Name = names.EventsTopic });

        foreach (var subscription in new[] { names.AuditSubscription, names.FraudSubscription, names.NotificationsSubscription })
        {
            await bus.Topics.CreateSubscriptionAsync(new SubscriptionDescriptor
            {
                TopicName = names.EventsTopic,
                Name = subscription,
            });
        }

        // A fresh subscription carries a $Default TrueFilter (same as Azure). Replacing it
        // by name is what real Azure setup code does too - just adding a second rule would
        // leave the match-all rule in place and the subscription would receive everything.
        await bus.Topics.CreateOrReplaceRuleAsync(new RuleDescriptor
        {
            TopicName = names.EventsTopic,
            SubscriptionName = names.FraudSubscription,
            Name = "$Default",
            Filter = new SqlFilter("amount >= 10000"),
        });

        await bus.Topics.CreateOrReplaceRuleAsync(new RuleDescriptor
        {
            TopicName = names.EventsTopic,
            SubscriptionName = names.NotificationsSubscription,
            Name = "$Default",
            Filter = new SqlFilter(
                "eventType IN ('transfer.completed', 'transfer.failed', 'account.frozen', 'payment.executed', 'payment.failed')"),
        });
    }
}
