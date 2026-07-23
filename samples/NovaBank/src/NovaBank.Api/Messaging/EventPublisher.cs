using System.Text.Json;
using Azure.Messaging.ServiceBus;

namespace NovaBank.Api.Messaging;

public interface IEventPublisher
{
    /// <summary>
    /// Publish a domain event to the events topic.
    /// <paramref name="settledAmount"/> is only set for *settled* money movements - it becomes
    /// the <c>amount</c> application property that the fraud subscription's SQL filter matches
    /// on (<c>amount &gt;= 10000</c>). Intent events (transfer.requested, payment.scheduled)
    /// deliberately do not carry it, so fraud only ever sees money that actually moved.
    /// </summary>
    Task PublishAsync(
        string eventType,
        object data,
        decimal? settledAmount = null,
        string? accountId = null,
        CancellationToken cancellationToken = default);
}

public sealed class ServiceBusEventPublisher : IEventPublisher
{
    private readonly BusSenders _senders;
    private readonly TimeProvider _time;

    public ServiceBusEventPublisher(BusSenders senders, TimeProvider time)
    {
        _senders = senders;
        _time = time;
    }

    public async Task PublishAsync(
        string eventType,
        object data,
        decimal? settledAmount = null,
        string? accountId = null,
        CancellationToken cancellationToken = default)
    {
        var evt = new IntegrationEvent(
            Guid.NewGuid().ToString("N"),
            eventType,
            _time.GetUtcNow(),
            JsonSerializer.SerializeToElement(data, BusJson.Options));

        var message = new ServiceBusMessage(BinaryData.FromObjectAsJson(evt, BusJson.Options))
        {
            MessageId = evt.EventId,
            Subject = eventType,
            ContentType = "application/json",
        };
        message.ApplicationProperties["eventType"] = eventType;
        if (settledAmount is not null)
        {
            // AMQP application properties have no decimal type; double is plenty for filter math.
            message.ApplicationProperties["amount"] = (double)settledAmount.Value;
        }
        if (accountId is not null)
        {
            message.ApplicationProperties["accountId"] = accountId;
        }

        await _senders.Events.SendMessageAsync(message, cancellationToken);
    }
}
