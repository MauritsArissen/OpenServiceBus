using Azure.Messaging.ServiceBus;
using OpenServiceBus.Explorer.Api;

namespace OpenServiceBus.Explorer.Tests;

/// <summary>
/// Pure-logic coverage for the resend building blocks (issue #28): the copy duplicates the
/// payload and user-facing metadata with fresh broker metadata, strips DLQ markers, and the
/// default destination derives from any DLQ address shape.
/// </summary>
public class ResendCopyTests
{
    private static ServiceBusReceivedMessage DeadLetteredOriginal() =>
        ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: BinaryData.FromString("payload"),
            messageId: "orig-id",
            sessionId: "session-7",
            partitionKey: "session-7",
            correlationId: "corr-1",
            subject: "hello",
            to: "to-addr",
            contentType: "text/plain",
            replyTo: "reply-addr",
            timeToLive: TimeSpan.FromMinutes(5),
            sequenceNumber: 42,
            deliveryCount: 9,
            deadLetterSource: "orders",
            properties: new Dictionary<string, object>
            {
                ["keep-me"] = "yes",
                ["DeadLetterReason"] = "boom",
                ["DeadLetterErrorDescription"] = "details",
                ["Diagnostic-Id"] = "00-trace",
            });

    [Fact]
    public void BuildResendCopy_DuplicatesUserMetadata_AndStripsDlqMarkers()
    {
        var copy = ExplorerEndpoints.BuildResendCopy(DeadLetteredOriginal(), keepMessageId: false);

        copy.Body.ToString().ShouldBe("payload");
        copy.SessionId.ShouldBe("session-7");
        copy.PartitionKey.ShouldBe("session-7");
        copy.CorrelationId.ShouldBe("corr-1");
        copy.Subject.ShouldBe("hello");
        copy.To.ShouldBe("to-addr");
        copy.ContentType.ShouldBe("text/plain");
        copy.ReplyTo.ShouldBe("reply-addr");
        copy.TimeToLive.ShouldBe(TimeSpan.FromMinutes(5));
        copy.ApplicationProperties["keep-me"].ShouldBe("yes");
        copy.ApplicationProperties.ContainsKey("DeadLetterReason").ShouldBeFalse();
        copy.ApplicationProperties.ContainsKey("DeadLetterErrorDescription").ShouldBeFalse();
        copy.ApplicationProperties.ContainsKey("Diagnostic-Id").ShouldBeFalse();
    }

    [Fact]
    public void BuildResendCopy_FreshId_MintsANewNonEmptyMessageId()
    {
        var copy = ExplorerEndpoints.BuildResendCopy(DeadLetteredOriginal(), keepMessageId: false);
        copy.MessageId.ShouldNotBeNullOrEmpty();
        copy.MessageId.ShouldNotBe("orig-id");

        var second = ExplorerEndpoints.BuildResendCopy(DeadLetteredOriginal(), keepMessageId: false);
        second.MessageId.ShouldNotBe(copy.MessageId, "every resend copy gets its own id");
    }

    [Fact]
    public void BuildResendCopy_KeepId_PreservesTheOriginalMessageId()
    {
        var copy = ExplorerEndpoints.BuildResendCopy(DeadLetteredOriginal(), keepMessageId: true);
        copy.MessageId.ShouldBe("orig-id");
    }

    [Fact]
    public void BuildResendCopy_EffectivelyInfiniteTtl_IsNotForwarded()
    {
        var original = ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: BinaryData.FromString("x"),
            messageId: "ttl-id",
            timeToLive: TimeSpan.FromDays(2000));

        var copy = ExplorerEndpoints.BuildResendCopy(original, keepMessageId: false);
        copy.TimeToLive.ShouldBe(TimeSpan.MaxValue, "an unset TTL reads back as MaxValue on ServiceBusMessage");
    }

    [Theory]
    [InlineData("orders/$DeadLetterQueue", "orders")]
    [InlineData("orders/$deadletterqueue", "orders")]
    [InlineData("events/Subscriptions/audit/$DeadLetterQueue", "events")]
    [InlineData("orders/$Transfer/$DeadLetterQueue", "orders")]
    [InlineData("events/Subscriptions/audit/$Transfer/$DeadLetterQueue", "events")]
    [InlineData("orders", null)]
    [InlineData("events/Subscriptions/audit", null)]
    public void ComputeResendTarget_DerivesTheSourceEntity(string dlqAddress, string? expected)
    {
        ExplorerEndpoints.ComputeResendTarget(dlqAddress).ShouldBe(expected);
    }
}
