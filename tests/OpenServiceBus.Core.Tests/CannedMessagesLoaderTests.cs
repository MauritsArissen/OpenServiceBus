using OpenServiceBus.Core.Configuration;

namespace OpenServiceBus.Core.Tests;

public class CannedMessagesLoaderTests
{
    [Fact]
    public void LoadFromJson_OneCannedMessage()
    {
        // Arrange
        const string json = """
            [
                {
                    "name": "json-sample",
                    "topicOrQueue": "*",
                    "message": {
                        "connectionString": "{{$auto}}",
                        "queue": "*",
                        "body": "{\n    \"myValue\": 1234\n}",
                        "messageId": null,
                        "correlationId": null,
                        "subject": null,
                        "contentType": "application/json",
                        "replyTo": null,
                        "to": null,
                        "sessionId": null,
                        "partitionKey": null,
                        "timeToLiveSeconds": null,
                        "scheduledEnqueueTime": null,
                        "properties": null,
                        "count": 1,
                        "strategy": "ATONCE"
                    }
                }
            ]
            """;

        // Act
        var result = CannedMessagesLoader.LoadFromJson(json);

        // Assert
        result.CannedMessages.Count.ShouldBe(1);
        var m = result.CannedMessages[0];
        m.Message.Queue.ShouldBe("*");
        m.Message.Body.ShouldBe("{\n    \"myValue\": 1234\n}");
        m.Message.MessageId.ShouldBe(null);
        m.Message.CorrelationId.ShouldBe(null);
        m.Message.Subject.ShouldBe(null);
        m.Message.ContentType.ShouldBe("application/json");
        m.Message.ReplyTo.ShouldBe(null);
        m.Message.To.ShouldBe(null);
        m.Message.SessionId.ShouldBe(null);
        m.Message.PartitionKey.ShouldBe(null);
        m.Message.TimeToLiveSeconds.ShouldBe(null);
        m.Message.ScheduledEnqueueTime.ShouldBe(null);
        m.Message.Properties.ShouldBe(null);
        m.Message.Count.ShouldBe(1);
        m.Message.Strategy.ShouldBe("ATONCE");
    }
}
