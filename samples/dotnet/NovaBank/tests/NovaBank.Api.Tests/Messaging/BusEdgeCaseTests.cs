using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NovaBank.Api.Infrastructure;
using NovaBank.Api.Messaging;
using OpenServiceBus.Core.Entities;
using OpenServiceBus.Testing;
using Shouldly;

namespace NovaBank.Api.Tests.Messaging;

/// <summary>Broker edge cases NovaBank depends on but no HTTP endpoint can reach.</summary>
public class BusEdgeCaseTests : IClassFixture<ServiceBusFixture>
{
    private readonly ServiceBusFixture _bus;

    public BusEdgeCaseTests(ServiceBusFixture bus) => _bus = bus;

    [Fact]
    public async Task SessionlessSend_ToTheSessionQueue_IsRejected_NotBlackholed()
    {
        // A payment instruction without a SessionId can never be picked up by a session
        // receiver - the broker must reject the send (like Azure), not swallow the message.
        var sender = _bus.Client.CreateSender(_bus.Names.PaymentsQueue);

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => sender.SendMessageAsync(new ServiceBusMessage("no session id")));
        ex.Message.ShouldContain("SessionId");

        (await _bus.Bus.Store.CountAsync(_bus.Names.PaymentsQueue)).ShouldBe(0L);
    }

    [Fact]
    public async Task SessionlessSchedule_ToTheSessionQueue_IsRejected()
    {
        var sender = _bus.Client.CreateSender(_bus.Names.PaymentsQueue);

        var ex = await Should.ThrowAsync<Exception>(() => sender.ScheduleMessageAsync(
            new ServiceBusMessage("no session id"), DateTimeOffset.UtcNow.AddMinutes(5)));
        ex.Message.ShouldContain("SessionId");

        (await _bus.Bus.Store.CountAsync(_bus.Names.PaymentsQueue)).ShouldBe(0L);
    }

    [Fact]
    public async Task PaymentsQueue_HasNoDuplicateDetection_SameMessageIdIsStoredTwice()
    {
        // Contrast with the transfers queue: idempotency is a per-entity opt-in, and the
        // payments queue deliberately doesn't have it (payment ids are unique per request).
        var sender = _bus.Client.CreateSender(_bus.Names.PaymentsQueue);
        var before = await _bus.Bus.Store.CountAsync(_bus.Names.PaymentsQueue);

        var id = Guid.NewGuid().ToString("N");
        await sender.SendMessageAsync(new ServiceBusMessage("one") { MessageId = id, SessionId = "ACC-DUP" });
        await sender.SendMessageAsync(new ServiceBusMessage("two") { MessageId = id, SessionId = "ACC-DUP" });

        (await _bus.Bus.Store.CountAsync(_bus.Names.PaymentsQueue)).ShouldBe(before + 2);

        // Drain so later tests in this class see a clean queue.
        var session = await _bus.Client.AcceptSessionAsync(_bus.Names.PaymentsQueue, "ACC-DUP");
        for (var i = 0; i < 2; i++)
        {
            var msg = await session.ReceiveMessageAsync(TimeSpan.FromSeconds(5));
            await session.CompleteMessageAsync(msg!);
        }
        await session.DisposeAsync();
    }

    [Fact]
    public async Task DuplicateDetectionWindow_Expires_ThenTheSameMessageIdIsAcceptedAgain()
    {
        // Time travel: a 10-minute dedup window tested in milliseconds.
        var clock = new FakeTimeProvider();
        await using var bus = await OpenServiceBusTestHost.StartAsync(o => o.TimeProvider = clock);
        await bus.Queues.CreateAsync(new QueueDescriptor
        {
            Name = "dedup-window",
            RequiresDuplicateDetection = true,
            DuplicateDetectionHistoryTimeWindow = TimeSpan.FromMinutes(10),
        });
        await using var client = new ServiceBusClient(bus.ConnectionString);
        var sender = client.CreateSender("dedup-window");

        await sender.SendMessageAsync(new ServiceBusMessage("first") { MessageId = "window-key" });
        await sender.SendMessageAsync(new ServiceBusMessage("duplicate") { MessageId = "window-key" });
        (await bus.Store.CountAsync("dedup-window")).ShouldBe(1L, "inside the window the repeat is dropped");

        clock.Advance(TimeSpan.FromMinutes(11));

        await sender.SendMessageAsync(new ServiceBusMessage("after window") { MessageId = "window-key" });
        (await bus.Store.CountAsync("dedup-window")).ShouldBe(2L, "the window is sliding, not forever");
    }

    [Fact]
    public async Task MalformedCommandBody_IsPoison_AndEndsUpDeadLettered()
    {
        // A non-JSON body makes the worker throw on every delivery - the broker must
        // retry it MaxDeliveryCount times and dead-letter it, never lose or loop it.
        var store = new InMemoryBankStore(TimeProvider.System);
        await using var senders = new BusSenders(_bus.Client, Options.Create(_bus.Names));
        var worker = new TransferWorker(
            _bus.Client,
            Options.Create(_bus.Names),
            store,
            new ServiceBusEventPublisher(senders, TimeProvider.System),
            NullLogger<TransferWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            var dlq = _bus.Names.TransfersQueue + "/$DeadLetterQueue";
            var dlqBefore = await _bus.Bus.Store.CountAsync(dlq);

            await senders.Transfers.SendMessageAsync(new ServiceBusMessage("this is not json {{{"));

            await Eventually.SatisfiesAsync(
                async () => await _bus.Bus.Store.CountAsync(dlq) == dlqBefore + 1,
                because: "garbage on the queue must dead-letter after MaxDeliveryCount attempts");
            (await _bus.Bus.Store.CountAsync(_bus.Names.TransfersQueue)).ShouldBe(0L);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
            worker.Dispose();
        }
    }
}
