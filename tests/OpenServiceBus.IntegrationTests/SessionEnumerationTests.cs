using Amqp;
using Amqp.Framing;
using Amqp.Sasl;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Time.Testing;
using OpenServiceBus.Core.Entities;
using OpenServiceBus.Testing;
using Map = Amqp.Types.Map;

namespace OpenServiceBus.IntegrationTests;

/// <summary>
/// End-to-end coverage for <c>com.microsoft:get-message-sessions</c> (issue #57) against the
/// full broker, with all session traffic and session state produced through the real Azure
/// SDK. No released Azure SDK exposes the session enumeration API yet (.NET ships it in
/// 7.21.0-beta.1), so the enumeration itself runs over a raw AMQP management link using the
/// exact request shape and paging algorithm of <c>ServiceBusClient.GetMessageSessionsAsync</c>:
/// pages of <c>top</c> (the SDK uses 100) advanced by <c>skip</c>, terminated by a 204 reply
/// or a short page, with <c>DateTime.MaxValue</c> as the no-filter sentinel.
/// </summary>
public class SessionEnumerationTests
{
    private const string Operation = "com.microsoft:get-message-sessions";
    private const int SdkPageSize = 100;

    private static async Task<List<string>> EnumerateSessionsAsync(
        string amqpUri, string entityPath, DateTime lastUpdatedTime, int pageSize = SdkPageSize, List<int>? pageSizes = null)
    {
        var factory = new ConnectionFactory();
        factory.SASL.Profile = SaslProfile.Anonymous;
        var conn = await factory.CreateAsync(new Address(amqpUri));
        try
        {
            var session = new Session(conn);
            var sender = new SenderLink(session, "mgmt-request", entityPath + "/$management");
            var receiver = new ReceiverLink(session, "mgmt-reply", entityPath + "/$management");

            var all = new List<string>();
            var skip = 0;
            while (true)
            {
                var request = new Message
                {
                    Properties = new Properties { MessageId = Guid.NewGuid().ToString() },
                    ApplicationProperties = new ApplicationProperties { ["operation"] = Operation },
                    BodySection = new AmqpValue
                    {
                        Value = new Map
                        {
                            ["last-updated-time"] = lastUpdatedTime,
                            ["skip"] = skip,
                            ["top"] = pageSize,
                        },
                    },
                };
                await sender.SendAsync(request);
                var response = await receiver.ReceiveAsync(TimeSpan.FromSeconds(10));
                response.ShouldNotBeNull("the $management node must always reply");
                receiver.Accept(response);

                var statusCode = Convert.ToInt32(response.ApplicationProperties!["statusCode"]);
                if (statusCode == 204) break;

                statusCode.ShouldBe(200);
                var body = response.Body.ShouldBeOfType<Map>();
                var ids = body["sessions-ids"] switch
                {
                    string[] strings => strings,
                    object[] objects => objects.Cast<string>().ToArray(),
                    var other => throw new InvalidOperationException($"unexpected sessions-ids payload: {other?.GetType().Name}"),
                };
                ids.Length.ShouldBeGreaterThan(0, "a 200 page must carry sessions");
                Convert.ToInt32(body["skip"]).ShouldBe(skip + ids.Length, "the reply skip is the next page cursor");
                pageSizes?.Add(ids.Length);
                all.AddRange(ids);
                if (ids.Length < pageSize) break;
                skip += ids.Length;

                all.Count.ShouldBeLessThanOrEqualTo(10_000, "paging must not loop forever");
            }

            await session.CloseAsync();
            return all;
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    [Fact]
    public async Task EnumerateSessions_ActiveSessionsAndStateOnlySession_AllReturnedAndTerminates()
    {
        // Arrange
        await using var host = await OpenServiceBusTestHost.StartAsync();
        await host.Queues.CreateAsync(new QueueDescriptor { Name = "sessioned", RequiresSession = true });
        await using var client = new ServiceBusClient(host.ConnectionString);
        var sender = client.CreateSender("sessioned");
        await sender.SendMessageAsync(new ServiceBusMessage("m1") { SessionId = "tenant-b", MessageId = "m1" });
        await sender.SendMessageAsync(new ServiceBusMessage("m2") { SessionId = "tenant-a", MessageId = "m2" });
        await sender.SendMessageAsync(new ServiceBusMessage("m3") { SessionId = "tenant-c", MessageId = "m3" });
        await sender.SendMessageAsync(new ServiceBusMessage("m4") { SessionId = "tenant-state", MessageId = "m4" });

        var stateSession = await client.AcceptSessionAsync("sessioned", "tenant-state");
        await stateSession.SetSessionStateAsync(BinaryData.FromString("checkpoint"));
        var toComplete = await stateSession.ReceiveMessageAsync(TimeSpan.FromSeconds(5));
        await stateSession.CompleteMessageAsync(toComplete);
        await stateSession.DisposeAsync();

        // Act
        var ids = await EnumerateSessionsAsync(host.AmqpUri, "sessioned", DateTime.MaxValue);

        // Assert
        ids.ToArray().ShouldBe(new[] { "tenant-a", "tenant-b", "tenant-c", "tenant-state" },
            "sessions with available messages and the state-only session, ordinal order");
    }

    [Fact]
    public async Task EnumerateSessions_MoreSessionsThanTheSdkPageSize_PagesThroughWithoutLooping()
    {
        // Arrange
        await using var host = await OpenServiceBusTestHost.StartAsync();
        await host.Queues.CreateAsync(new QueueDescriptor { Name = "many", RequiresSession = true });
        await using var client = new ServiceBusClient(host.ConnectionString);
        var sender = client.CreateSender("many");
        var expected = new List<string>();
        for (var batchStart = 0; batchStart < 120; batchStart += 40)
        {
            var batch = new List<ServiceBusMessage>();
            for (var i = batchStart; i < batchStart + 40; i++)
            {
                var sessionId = $"s-{i:D3}";
                expected.Add(sessionId);
                batch.Add(new ServiceBusMessage($"m-{i}") { SessionId = sessionId, MessageId = $"m-{i}" });
            }
            await sender.SendMessagesAsync(batch);
        }
        expected.Sort(StringComparer.Ordinal);

        // Act
        var pageSizes = new List<int>();
        var ids = await EnumerateSessionsAsync(host.AmqpUri, "many", DateTime.MaxValue, SdkPageSize, pageSizes);

        // Assert
        ids.ToArray().ShouldBe(expected.ToArray(), "every session exactly once, no duplicates, no infinite loop");
        pageSizes.SequenceEqual(new[] { 100, 20 }).ShouldBeTrue("120 sessions come back as a full page plus a short final page");
    }

    [Fact]
    public async Task EnumerateSessions_LastUpdatedTimeFilter_HonorsSessionStateUpdateInstant()
    {
        // Arrange
        var clock = new FakeTimeProvider();
        await using var host = await OpenServiceBusTestHost.StartAsync(o => o.TimeProvider = clock);
        await host.Queues.CreateAsync(new QueueDescriptor { Name = "filtered", RequiresSession = true });
        await using var client = new ServiceBusClient(host.ConnectionString);
        var sender = client.CreateSender("filtered");
        await sender.SendMessageAsync(new ServiceBusMessage("m1") { SessionId = "s-old", MessageId = "m1" });
        await sender.SendMessageAsync(new ServiceBusMessage("m2") { SessionId = "s-new", MessageId = "m2" });
        await sender.SendMessageAsync(new ServiceBusMessage("m3") { SessionId = "s-no-state", MessageId = "m3" });

        var oldSession = await client.AcceptSessionAsync("filtered", "s-old");
        await oldSession.SetSessionStateAsync(BinaryData.FromString("old"));
        await oldSession.DisposeAsync();

        var cutoff = clock.GetUtcNow();
        clock.Advance(TimeSpan.FromMinutes(5));

        var newSession = await client.AcceptSessionAsync("filtered", "s-new");
        await newSession.SetSessionStateAsync(BinaryData.FromString("new"));
        await newSession.DisposeAsync();

        // Act
        var filtered = await EnumerateSessionsAsync(host.AmqpUri, "filtered", cutoff.UtcDateTime);
        var unfiltered = await EnumerateSessionsAsync(host.AmqpUri, "filtered", DateTime.MaxValue);

        // Assert
        filtered.ToArray().ShouldBe(new[] { "s-new" }, "only session state updated after the cutoff matches");
        unfiltered.ToArray().ShouldBe(new[] { "s-new", "s-no-state", "s-old" });
    }

    [Fact]
    public async Task EnumerateSessions_OnSessionEnabledSubscription_ReturnsItsSessions()
    {
        // Arrange
        await using var host = await OpenServiceBusTestHost.StartAsync();
        await host.Topics.CreateTopicAsync(new TopicDescriptor { Name = "events" });
        await host.Topics.CreateSubscriptionAsync(new SubscriptionDescriptor
        {
            TopicName = "events",
            Name = "by-tenant",
            RequiresSession = true,
        });
        await using var client = new ServiceBusClient(host.ConnectionString);
        var sender = client.CreateSender("events");
        await sender.SendMessageAsync(new ServiceBusMessage("a") { SessionId = "alpha", MessageId = "a" });
        await sender.SendMessageAsync(new ServiceBusMessage("b") { SessionId = "beta", MessageId = "b" });

        var stateSession = await client.AcceptSessionAsync("events", "by-tenant", "beta");
        await stateSession.SetSessionStateAsync(BinaryData.FromString("cursor"));
        var received = await stateSession.ReceiveMessageAsync(TimeSpan.FromSeconds(5));
        await stateSession.CompleteMessageAsync(received);
        await stateSession.DisposeAsync();

        // Act
        var ids = await EnumerateSessionsAsync(host.AmqpUri, "events/Subscriptions/by-tenant", DateTime.MaxValue);

        // Assert
        ids.ToArray().ShouldBe(new[] { "alpha", "beta" },
            "the active session and the state-only session on the subscription");
    }
}
