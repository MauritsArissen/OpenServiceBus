using Amqp;
using Amqp.Framing;
using Amqp.Sasl;
using OpenServiceBus.Core.Entities;
using Map = Amqp.Types.Map;

namespace OpenServiceBus.Amqp.WireTests;

public class CbsTests
{
    private static ConnectionFactory CreateClientFactory()
    {
        var factory = new ConnectionFactory();
        factory.SASL.Profile = SaslProfile.Anonymous;
        return factory;
    }

    [Fact]
    public async Task PutToken_ReplyLinkWithoutTargetAddress_RheaShape_StillGetsResponse()
    {
        // Regression for GitHub issue #1: rhea-based SDKs (Node.js @azure/service-bus,
        // Python) attach their $cbs reply link with an EMPTY target address and set the
        // request's reply-to to the reply link's NAME. AMQPNetLite used to throw
        // ArgumentNullException registering that link (null dictionary key), detaching it
        // with amqp:internal-error - CBS auth never completed and every SDK call timed out.

        // Arrange
        await using var harness = await TestListenerHarness.StartAsync();
        var factory = CreateClientFactory();
        var conn = await factory.CreateAsync(new Address(harness.AmqpUri));
        try
        {
            var session = new Session(conn);
            var linkName = "cbs-" + Guid.NewGuid().ToString("N");
            var receiver = new ReceiverLink(session, linkName, new Attach
            {
                Source = new Source { Address = "$cbs" },
                Target = new Target(), // rhea shape: no target address at all
            }, null);
            receiver.SetCredit(10, true);
            var sender = new SenderLink(session, "cbs-sender-" + Guid.NewGuid().ToString("N"), new Attach
            {
                Source = new Source(),
                Target = new Target { Address = "$cbs" },
            }, null);

            var requestId = Guid.NewGuid().ToString("N");
            var request = new Message("sas-token-payload")
            {
                Properties = new Properties
                {
                    MessageId = requestId,
                    ReplyTo = linkName, // rhea: reply-to == reply link name
                },
                ApplicationProperties = new ApplicationProperties(),
            };
            request.ApplicationProperties["operation"] = "put-token";
            request.ApplicationProperties["type"] = "servicebus.windows.net:sastoken";
            request.ApplicationProperties["name"] = "amqp://localhost/myqueue";

            // Act
            await sender.SendAsync(request);
            var response = await receiver.ReceiveAsync(TimeSpan.FromSeconds(5));

            // Assert
            response.ShouldNotBeNull("the rhea-shaped reply link must still receive the CBS response");
            receiver.Accept(response);
            response.Properties.CorrelationId.ShouldBe(requestId);
            ((int)response.ApplicationProperties["status-code"]).ShouldBe(202);

            await sender.CloseAsync();
            await receiver.CloseAsync();
            await session.CloseAsync();
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    [Fact]
    public async Task PutToken_UlongMessageId_ProtonJShape_StillGetsCorrelatedResponse()
    {
        // Regression for GitHub issue #1 (Java leg): proton-j sends CBS message-ids as AMQP
        // ulong (spec-legal). AMQPNetLite's RequestContext.Complete reads the string-typed
        // MessageId getter and threw InvalidCastException, so the response was never sent
        // and every Java SDK operation timed out.

        // Arrange
        await using var harness = await TestListenerHarness.StartAsync();
        var factory = CreateClientFactory();
        var conn = await factory.CreateAsync(new Address(harness.AmqpUri));
        try
        {
            var session = new Session(conn);
            var replyAddress = "cbs-client-reply-to"; // proton-j uses this fixed name
            var receiver = new ReceiverLink(session, "cbs:receiver", new Attach
            {
                Source = new Source { Address = "$cbs" },
                Target = new Target { Address = replyAddress },
            }, null);
            receiver.SetCredit(10, true);
            var sender = new SenderLink(session, "cbs:sender", new Attach
            {
                Source = new Source(),
                Target = new Target { Address = "$cbs" },
            }, null);

            var request = new Message("sas-token-payload")
            {
                Properties = new Properties { ReplyTo = replyAddress },
                ApplicationProperties = new ApplicationProperties(),
            };
            request.Properties.SetMessageId(7UL); // the proton-j shape: ulong, not string
            request.ApplicationProperties["operation"] = "put-token";
            request.ApplicationProperties["type"] = "servicebus.windows.net:sastoken";
            request.ApplicationProperties["name"] = "amqp://localhost/myqueue";

            // Act
            await sender.SendAsync(request);
            var response = await receiver.ReceiveAsync(TimeSpan.FromSeconds(5));

            // Assert
            response.ShouldNotBeNull("a ulong message-id must not prevent the CBS response");
            receiver.Accept(response);
            response.Properties.GetCorrelationId().ShouldBe(7UL);
            ((int)response.ApplicationProperties["status-code"]).ShouldBe(202);

            await sender.CloseAsync();
            await receiver.CloseAsync();
            await session.CloseAsync();
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    [Fact]
    public async Task Management_ReplyLinkWithoutTargetAddress_RheaShape_StillGetsResponse()
    {
        // Same rhea shape as above, but against an entity's $management node - the Node SDK
        // uses the identical request/response pattern for peek/schedule/renew-lock.

        // Arrange
        await using var harness = await TestListenerHarness.StartAsync();
        await harness.Queues.CreateAsync(new QueueDescriptor { Name = "rhea-mgmt" });
        var factory = CreateClientFactory();
        var conn = await factory.CreateAsync(new Address(harness.AmqpUri));
        try
        {
            var session = new Session(conn);
            var linkName = "mgmt-" + Guid.NewGuid().ToString("N");
            var receiver = new ReceiverLink(session, linkName, new Attach
            {
                Source = new Source { Address = "rhea-mgmt/$management" },
                Target = new Target(), // no target address
            }, null);
            receiver.SetCredit(10, true);
            var sender = new SenderLink(session, "mgmt-sender-" + Guid.NewGuid().ToString("N"), new Attach
            {
                Source = new Source(),
                Target = new Target { Address = "rhea-mgmt/$management" },
            }, null);

            var requestId = Guid.NewGuid().ToString("N");
            var request = new Message(new Map
            {
                ["from-sequence-number"] = 0L,
                ["message-count"] = 1,
            })
            {
                Properties = new Properties { MessageId = requestId, ReplyTo = linkName },
                ApplicationProperties = new ApplicationProperties(),
            };
            request.ApplicationProperties["operation"] = "com.microsoft:peek-message";

            // Act - peek on an empty queue must still produce a (204) response.
            await sender.SendAsync(request);
            var response = await receiver.ReceiveAsync(TimeSpan.FromSeconds(5));

            // Assert
            response.ShouldNotBeNull("the rhea-shaped reply link must receive the $management response");
            receiver.Accept(response);
            response.Properties.CorrelationId.ShouldBe(requestId);
            ((int)response.ApplicationProperties["statusCode"]).ShouldBe(204);

            await sender.CloseAsync();
            await receiver.CloseAsync();
            await session.CloseAsync();
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    [Fact]
    public async Task PutToken_ValidRequest_Returns202AcceptedWithCorrelationIdAndKebabCaseKeys()
    {
        // Arrange
        await using var harness = await TestListenerHarness.StartAsync();
        var factory = CreateClientFactory();
        var conn = await factory.CreateAsync(new Address(harness.AmqpUri));
        try
        {
            var session = new Session(conn);
            var replyAddress = "cbs-reply-" + Guid.NewGuid().ToString("N");
            var receiver = new ReceiverLink(session, "cbs-receiver", new Attach
            {
                Source = new Source { Address = "$cbs" },
                Target = new Target { Address = replyAddress },
            }, null);
            receiver.SetCredit(10, true);
            var sender = new SenderLink(session, "cbs-sender", new Attach
            {
                Source = new Source { Address = replyAddress },
                Target = new Target { Address = "$cbs" },
            }, null);
            var requestId = Guid.NewGuid().ToString("N");
            var request = new Message("sas-token-payload")
            {
                Properties = new Properties
                {
                    MessageId = requestId,
                    ReplyTo = replyAddress,
                },
                ApplicationProperties = new ApplicationProperties(),
            };
            request.ApplicationProperties["operation"] = "put-token";
            request.ApplicationProperties["type"] = "servicebus.windows.net:sastoken";
            request.ApplicationProperties["name"] = "amqp://localhost/myqueue";
            request.ApplicationProperties["expiration"] = DateTime.UtcNow.AddHours(1);

            // Act
            await sender.SendAsync(request);
            var response = await receiver.ReceiveAsync(TimeSpan.FromSeconds(5));

            // Assert
            response.ShouldNotBeNull("expected a CBS put-token response within 5s");
            receiver.Accept(response);

            response.Properties.ShouldNotBeNull();
            response.Properties.CorrelationId.ShouldBe(requestId);

            response.ApplicationProperties.ShouldNotBeNull();
            response.ApplicationProperties.Map.ContainsKey("status-code").ShouldBeTrue("response must use kebab-case 'status-code'");
            response.ApplicationProperties.Map.ContainsKey("status-description").ShouldBeTrue("response must use kebab-case 'status-description'");
            response.ApplicationProperties.Map.ContainsKey("statusCode").ShouldBeFalse("response must NOT use camelCase 'statusCode' for CBS");

            ((int)response.ApplicationProperties["status-code"]).ShouldBe(202);
            ((string)response.ApplicationProperties["status-description"]).ShouldBe("Accepted");

            await sender.CloseAsync();
            await receiver.CloseAsync();
            await session.CloseAsync();
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    [Fact]
    public async Task PutToken_FiveSequentialRequests_EchoesCorrelationIdForEachOne()
    {
        // Arrange
        await using var harness = await TestListenerHarness.StartAsync();
        var factory = CreateClientFactory();
        var conn = await factory.CreateAsync(new Address(harness.AmqpUri));
        try
        {
            var session = new Session(conn);
            var replyAddress = "cbs-reply-" + Guid.NewGuid().ToString("N");
            var receiver = new ReceiverLink(session, "cbs-receiver", new Attach
            {
                Source = new Source { Address = "$cbs" },
                Target = new Target { Address = replyAddress },
            }, null);
            receiver.SetCredit(10, true);
            var sender = new SenderLink(session, "cbs-sender", new Attach
            {
                Source = new Source { Address = replyAddress },
                Target = new Target { Address = "$cbs" },
            }, null);

            // Act + Assert (per-iteration verification)
            for (var i = 0; i < 5; i++)
            {
                var msgId = $"req-{i}-{Guid.NewGuid():N}";
                var req = new Message("payload")
                {
                    Properties = new Properties { MessageId = msgId, ReplyTo = replyAddress },
                    ApplicationProperties = new ApplicationProperties(),
                };
                req.ApplicationProperties["operation"] = "put-token";
                await sender.SendAsync(req);
                var resp = await receiver.ReceiveAsync(TimeSpan.FromSeconds(5));
                resp.ShouldNotBeNull();
                receiver.Accept(resp);
                resp.Properties.CorrelationId.ShouldBe(msgId, $"iteration {i}");
            }

            await sender.CloseAsync();
            await receiver.CloseAsync();
            await session.CloseAsync();
        }
        finally
        {
            await conn.CloseAsync();
        }
    }
}
