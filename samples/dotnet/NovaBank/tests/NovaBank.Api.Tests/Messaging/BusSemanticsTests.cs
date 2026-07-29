using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Time.Testing;
using NovaBank.Api.Messaging;
using OpenServiceBus.Core.Entities;
using OpenServiceBus.Testing;
using Shouldly;

namespace NovaBank.Api.Tests.Messaging;

/// <summary>
/// The broker guarantees NovaBank leans on, proven at the wire level: duplicate detection,
/// per-session FIFO, and broker-held scheduling - the last one with a fake clock, so a
/// "payment due in 5 minutes" test finishes in milliseconds.
/// </summary>
public class BusSemanticsTests : IClassFixture<ServiceBusFixture>
{
    private readonly ServiceBusFixture _bus;

    public BusSemanticsTests(ServiceBusFixture bus) => _bus = bus;

    [Fact]
    public async Task TransfersQueue_DropsDuplicateMessageIds_Silently()
    {
        var sender = _bus.Client.CreateSender(_bus.Names.TransfersQueue);
        var key = Guid.NewGuid().ToString("N");

        // Same MessageId twice - e.g. an API instance re-sending after a crash. Both sends
        // are ACCEPTED by the broker (the sender can't tell), but only one copy is stored.
        await sender.SendMessageAsync(new ServiceBusMessage("attempt-1") { MessageId = key });
        await sender.SendMessageAsync(new ServiceBusMessage("attempt-2") { MessageId = key });

        (await _bus.Bus.Store.CountAsync(_bus.Names.TransfersQueue)).ShouldBe(1L);

        var receiver = _bus.Client.CreateReceiver(_bus.Names.TransfersQueue);
        var msg = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(5));
        msg.ShouldNotBeNull();
        msg!.Body.ToString().ShouldBe("attempt-1", "the first send wins; the duplicate is dropped");
        await receiver.CompleteMessageAsync(msg);

        (await receiver.ReceiveMessageAsync(TimeSpan.FromMilliseconds(500))).ShouldBeNull();
    }

    [Fact]
    public async Task PaymentsQueue_PreservesFifoWithinASession_AcrossSessionsIndependently()
    {
        var sender = _bus.Client.CreateSender(_bus.Names.PaymentsQueue);

        // Interleave two accounts' payments on purpose.
        foreach (var (session, n) in new[] { ("ACC-FIFO-A", 1), ("ACC-FIFO-B", 1), ("ACC-FIFO-A", 2), ("ACC-FIFO-B", 2), ("ACC-FIFO-A", 3) })
        {
            await sender.SendMessageAsync(new ServiceBusMessage($"{session}:{n}") { SessionId = session });
        }

        var sessionA = await _bus.Client.AcceptSessionAsync(_bus.Names.PaymentsQueue, "ACC-FIFO-A");
        var got = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var msg = await sessionA.ReceiveMessageAsync(TimeSpan.FromSeconds(5));
            msg.ShouldNotBeNull();
            got.Add(msg!.Body.ToString());
            await sessionA.CompleteMessageAsync(msg);
        }
        await sessionA.DisposeAsync();

        // Session A only sees its own messages, strictly in send order - B's interleaved
        // sends don't leak in and don't reorder anything.
        got.ShouldBe(new[] { "ACC-FIFO-A:1", "ACC-FIFO-A:2", "ACC-FIFO-A:3" });

        var sessionB = await _bus.Client.AcceptSessionAsync(_bus.Names.PaymentsQueue, "ACC-FIFO-B");
        (await sessionB.ReceiveMessageAsync(TimeSpan.FromSeconds(5)))!.Body.ToString().ShouldBe("ACC-FIFO-B:1");
        await sessionB.DisposeAsync();
    }

    [Fact]
    public async Task ScheduledPayment_TimeTravel_FiveMinutesInMilliseconds()
    {
        // Separate broker with a fake clock: OpenServiceBusTestHost runs every timer, TTL,
        // and scheduled-activation check on the injected TimeProvider.
        var clock = new FakeTimeProvider();
        await using var bus = await OpenServiceBusTestHost.StartAsync(o => o.TimeProvider = clock);
        await bus.Queues.CreateAsync(new QueueDescriptor
        {
            Name = "standing-orders",
            RequiresSession = true,
        });
        await using var client = new ServiceBusClient(bus.ConnectionString);

        var instruction = new PaymentInstruction("PAY-TT", "ACC-TT", "Energy Co", "NL91ABNA0417164300", 75m, "EUR", "monthly");
        var sender = client.CreateSender("standing-orders");
        await sender.ScheduleMessageAsync(
            new ServiceBusMessage(BinaryData.FromObjectAsJson(instruction, BusJson.Options)) { SessionId = "ACC-TT" },
            clock.GetUtcNow().AddMinutes(5));

        // Before the due time: the session exists but the broker holds the message back.
        var earlyCheck = await client.AcceptSessionAsync("standing-orders", "ACC-TT");
        (await earlyCheck.ReceiveMessageAsync(TimeSpan.FromMilliseconds(300)))
            .ShouldBeNull("a standing order must not execute early");
        await earlyCheck.DisposeAsync();

        // Jump the broker clock past the due time - no Task.Delay(5 minutes) anywhere.
        clock.Advance(TimeSpan.FromMinutes(6));

        // The jump also blew past the session lock's expiry - on real Azure that receiver
        // would be session-lock-lost, and the broker enforces the same. Accept the session
        // fresh, exactly as a worker picking up the standing order at its due time would.
        var session = await client.AcceptSessionAsync("standing-orders", "ACC-TT");

        ServiceBusReceivedMessage? due = null;
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (due is null && DateTime.UtcNow < deadline)
        {
            due = await session.ReceiveMessageAsync(TimeSpan.FromMilliseconds(500));
        }

        due.ShouldNotBeNull("advancing the clock must activate the scheduled message");
        var received = due!.Body.ToObjectFromJson<PaymentInstruction>(BusJson.Options);
        received!.PaymentId.ShouldBe("PAY-TT");
        due.SessionId.ShouldBe("ACC-TT");
        await session.CompleteMessageAsync(due);
    }
}
