using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Options;
using NovaBank.Api.Messaging;
using OpenServiceBus.Core.Entities;
using Shouldly;

namespace NovaBank.Api.Tests.Messaging;

/// <summary>
/// Tests <see cref="ServiceBusEventPublisher"/> against the broker directly: message shape
/// on the wire, and - the part unit tests usually can't cover - which subscriptions the
/// broker's SQL filters actually route each event to.
/// </summary>
public class EventPublisherBusTests : IClassFixture<ServiceBusFixture>, IAsyncLifetime
{
    private readonly ServiceBusFixture _bus;
    private BusSenders _senders = null!;
    private ServiceBusEventPublisher _publisher = null!;

    public EventPublisherBusTests(ServiceBusFixture bus) => _bus = bus;

    public Task InitializeAsync()
    {
        _senders = new BusSenders(_bus.Client, Options.Create(_bus.Names));
        _publisher = new ServiceBusEventPublisher(_senders, TimeProvider.System);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _senders.DisposeAsync();

    [Fact]
    public async Task PublishAsync_PutsAWellFormedEventOnTheTopic()
    {
        await _publisher.PublishAsync(
            EventTypes.TransferCompleted,
            new { transferId = "TRF-SHAPE", amount = 42.50m, currency = "EUR" },
            settledAmount: 42.50m,
            accountId: "ACC-SHAPE");

        var receiver = _bus.Client.CreateReceiver(_bus.Names.EventsTopic, _bus.Names.AuditSubscription);
        var msg = await ServiceBusFixture.ReceiveUntilAsync(receiver,
            m => m.Body.ToString().Contains("TRF-SHAPE"));

        msg.ShouldNotBeNull();
        msg!.Subject.ShouldBe("transfer.completed");
        msg.ContentType.ShouldBe("application/json");
        msg.ApplicationProperties["eventType"].ShouldBe("transfer.completed");
        msg.ApplicationProperties["accountId"].ShouldBe("ACC-SHAPE");
        msg.ApplicationProperties["amount"].ShouldBe(42.50d);

        var envelope = msg.Body.ToObjectFromJson<IntegrationEvent>(BusJson.Options);
        envelope.ShouldNotBeNull();
        envelope!.EventType.ShouldBe("transfer.completed");
        envelope.EventId.ShouldBe(msg.MessageId);
        envelope.Data.GetProperty("transferId").GetString().ShouldBe("TRF-SHAPE");
        envelope.Data.GetProperty("amount").GetDecimal().ShouldBe(42.50m);
    }

    [Fact]
    public async Task SmallMovement_ReachesAudit_ButTheBrokerFilterKeepsItAwayFromFraud()
    {
        var fraudQueue = EntityNames.SubscriptionAddress(_bus.Names.EventsTopic, _bus.Names.FraudSubscription);
        var fraudBefore = await _bus.Bus.Store.CountAsync(fraudQueue);

        await _publisher.PublishAsync(
            EventTypes.TransferCompleted,
            new { transferId = "TRF-SMALL", amount = 500m },
            settledAmount: 500m,
            accountId: "ACC-SMALL");

        // Audit (match-all) sees it...
        var audit = _bus.Client.CreateReceiver(_bus.Names.EventsTopic, _bus.Names.AuditSubscription);
        (await ServiceBusFixture.ReceiveUntilAsync(audit, m => m.Body.ToString().Contains("TRF-SMALL")))
            .ShouldNotBeNull();

        // ...and the broker-side SQL filter (amount >= 10000) never even enqueued a copy
        // for fraud - asserted straight against the subscription's backing queue.
        (await _bus.Bus.Store.CountAsync(fraudQueue)).ShouldBe(fraudBefore);
    }

    [Fact]
    public async Task LargeMovement_IsFannedOutToFraud_WithTheAmountProperty()
    {
        await _publisher.PublishAsync(
            EventTypes.TransferCompleted,
            new { transferId = "TRF-LARGE", amount = 15_000m },
            settledAmount: 15_000m,
            accountId: "ACC-LARGE");

        var fraud = _bus.Client.CreateReceiver(_bus.Names.EventsTopic, _bus.Names.FraudSubscription);
        var msg = await ServiceBusFixture.ReceiveUntilAsync(fraud, m => m.Body.ToString().Contains("TRF-LARGE"));

        msg.ShouldNotBeNull();
        msg!.ApplicationProperties["amount"].ShouldBe(15_000d);
        msg.ApplicationProperties["accountId"].ShouldBe("ACC-LARGE");
    }

    [Fact]
    public async Task IntentEvents_CarryNoAmountProperty_SoFraudNeverSeesThem()
    {
        var fraudQueue = EntityNames.SubscriptionAddress(_bus.Names.EventsTopic, _bus.Names.FraudSubscription);
        var fraudBefore = await _bus.Bus.Store.CountAsync(fraudQueue);

        // A 1M transfer *request* - not settled money. No settledAmount => no amount property
        // => the fraud filter's "amount >= 10000" can't match (SQL null semantics).
        await _publisher.PublishAsync(
            EventTypes.TransferRequested,
            new { transferId = "TRF-INTENT", requestedAmount = 1_000_000m },
            accountId: "ACC-INTENT");

        var audit = _bus.Client.CreateReceiver(_bus.Names.EventsTopic, _bus.Names.AuditSubscription);
        (await ServiceBusFixture.ReceiveUntilAsync(audit, m => m.Body.ToString().Contains("TRF-INTENT")))
            .ShouldNotBeNull();
        (await _bus.Bus.Store.CountAsync(fraudQueue)).ShouldBe(fraudBefore);
    }

    [Fact]
    public async Task NotificationsFilter_SelectsByEventType()
    {
        // account.deposited is not in the notifications IN-list...
        await _publisher.PublishAsync(
            EventTypes.AccountDeposited,
            new { accountId = "ACC-N1", amount = 10m },
            settledAmount: 10m,
            accountId: "ACC-N1");
        // ...but account.frozen is. The subscription is FIFO, so if the deposited event had
        // matched the filter it would necessarily be received BEFORE the frozen one.
        await _publisher.PublishAsync(
            EventTypes.AccountFrozen,
            new { accountId = "ACC-N1", reason = "test" },
            accountId: "ACC-N1");

        var notifications = _bus.Client.CreateReceiver(_bus.Names.EventsTopic, _bus.Names.NotificationsSubscription);
        var seen = new List<string>();
        var frozen = await ServiceBusFixture.ReceiveUntilAsync(notifications, m =>
        {
            seen.Add((string)m.ApplicationProperties["eventType"]);
            return m.Body.ToString().Contains("ACC-N1") &&
                   (string)m.ApplicationProperties["eventType"] == "account.frozen";
        });

        frozen.ShouldNotBeNull("account.frozen matches the notifications filter");
        seen.ShouldNotContain("account.deposited", "the broker filter must drop non-listed event types");
    }
}
