using Amqp;
using Amqp.Framing;
using Amqp.Sasl;
using Amqp.Types;
using Microsoft.Extensions.Time.Testing;
using OpenServiceBus.Core.Entities;
using Map = Amqp.Types.Map;

namespace OpenServiceBus.Amqp.WireTests;

/// <summary>
/// Wire-contract tests for <c>com.microsoft:get-message-sessions</c> (issue #57): the
/// request's <c>last-updated-time</c>/<c>skip</c>/<c>top</c> fields, the reply's
/// <c>sessions-ids</c> + <c>skip</c> body map and the 204 no-more-sessions terminator.
/// The paging loop below mirrors the official SDKs' enumeration algorithm (page with
/// skip/top, stop on 204 or a short page), which no released SDK exposes publicly yet.
/// </summary>
public class GetMessageSessionsWireTests
{
    private const string Operation = "com.microsoft:get-message-sessions";

    private static ConnectionFactory CreateClientFactory()
    {
        var factory = new ConnectionFactory();
        factory.SASL.Profile = SaslProfile.Anonymous;
        return factory;
    }

    private sealed record MgmtResponse(int StatusCode, string[] SessionIds, int? Skip, string? ErrorCondition);

    private static async Task<MgmtResponse> GetMessageSessionsAsync(
        Session session, string entity, DateTime? lastUpdatedTime, int? skip, int? top)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var sender = new SenderLink(session, "mgmt-request-" + suffix, entity + "/$management");
        var receiver = new ReceiverLink(session, "mgmt-reply-" + suffix, entity + "/$management");
        try
        {
            Map? body = null;
            if (lastUpdatedTime is not null || skip is not null || top is not null)
            {
                body = new Map();
                if (lastUpdatedTime is not null) body["last-updated-time"] = lastUpdatedTime.Value;
                if (skip is not null) body["skip"] = skip.Value;
                if (top is not null) body["top"] = top.Value;
            }
            var request = new Message
            {
                Properties = new Properties { MessageId = Guid.NewGuid().ToString() },
                ApplicationProperties = new ApplicationProperties { ["operation"] = Operation },
            };
            if (body is not null) request.BodySection = new AmqpValue { Value = body };

            await sender.SendAsync(request);
            var response = await receiver.ReceiveAsync(TimeSpan.FromSeconds(10));
            response.ShouldNotBeNull("the $management node must always reply");
            receiver.Accept(response);

            var statusCode = Convert.ToInt32(response.ApplicationProperties!["statusCode"]);
            var errorCondition = response.ApplicationProperties["errorCondition"]?.ToString();
            var sessionIds = Array.Empty<string>();
            int? replySkip = null;
            if (response.Body is Map responseBody)
            {
                if (responseBody.TryGetValue("sessions-ids", out var idsObj))
                {
                    sessionIds = idsObj switch
                    {
                        string[] strings => strings,
                        object[] objects => objects.Cast<string>().ToArray(),
                        _ => throw new InvalidOperationException($"unexpected sessions-ids payload: {idsObj?.GetType().Name}"),
                    };
                }
                if (responseBody.TryGetValue("skip", out var skipObj))
                {
                    replySkip = Convert.ToInt32(skipObj);
                }
            }
            return new MgmtResponse(statusCode, sessionIds, replySkip, errorCondition);
        }
        finally
        {
            await sender.CloseAsync();
            await receiver.CloseAsync();
        }
    }

    private static async Task<List<string>> EnumerateLikeTheSdkAsync(
        Session session, string entity, DateTime lastUpdatedTime, int pageSize, List<int>? pageSizes = null)
    {
        var all = new List<string>();
        var skip = 0;
        while (true)
        {
            var page = await GetMessageSessionsAsync(session, entity, lastUpdatedTime, skip, pageSize);
            if (page.StatusCode == 204) break;

            page.StatusCode.ShouldBe(200);
            page.SessionIds.Length.ShouldBeGreaterThan(0, "a 200 page must carry sessions");
            page.Skip.ShouldBe(skip + page.SessionIds.Length, "the reply skip is the next page cursor");
            pageSizes?.Add(page.SessionIds.Length);
            all.AddRange(page.SessionIds);
            if (page.SessionIds.Length < pageSize) break;
            skip = page.Skip!.Value;

            all.Count.ShouldBeLessThanOrEqualTo(10_000, "paging must not loop forever");
        }
        return all;
    }

    [Fact]
    public async Task GetMessageSessions_ActiveAndStateOnlySessions_ReturnsAllWithPagingMetadata()
    {
        // Arrange
        await using var harness = await TestListenerHarness.StartAsync();
        await harness.Queues.CreateAsync(new QueueDescriptor { Name = "q", RequiresSession = true });
        await harness.Store.EnqueueAsync("q", [1], sessionId: "s-b");
        await harness.Store.EnqueueAsync("q", [2], sessionId: "s-a");
        await harness.Store.EnqueueAsync("q", [3], sessionId: "s-c");
        await harness.Store.SetSessionStateAsync("q", "s-state-only", [9]);

        var conn = await CreateClientFactory().CreateAsync(new Address(harness.AmqpUri));
        try
        {
            var session = new Session(conn);

            // Act
            var page = await GetMessageSessionsAsync(session, "q", DateTime.MaxValue, skip: 0, top: 100);
            var beyond = await GetMessageSessionsAsync(session, "q", DateTime.MaxValue, skip: 4, top: 100);

            // Assert
            page.StatusCode.ShouldBe(200);
            page.SessionIds.ShouldBe(new[] { "s-a", "s-b", "s-c", "s-state-only" });
            page.Skip.ShouldBe(4);
            beyond.StatusCode.ShouldBe(204, "a page past the end signals no-more-sessions");
            beyond.ErrorCondition.ShouldBe("com.microsoft:session-not-found");

            await session.CloseAsync();
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    [Fact]
    public async Task GetMessageSessions_MoreSessionsThanOnePage_SdkStylePagingTerminates()
    {
        // Arrange
        await using var harness = await TestListenerHarness.StartAsync();
        await harness.Queues.CreateAsync(new QueueDescriptor { Name = "q", RequiresSession = true });
        for (var i = 0; i < 5; i++)
        {
            await harness.Store.EnqueueAsync("q", [1], sessionId: $"s-{i}");
        }

        var conn = await CreateClientFactory().CreateAsync(new Address(harness.AmqpUri));
        try
        {
            var session = new Session(conn);

            // Act
            var oddPages = new List<int>();
            var odd = await EnumerateLikeTheSdkAsync(session, "q", DateTime.MaxValue, pageSize: 2, oddPages);
            var exactPages = new List<int>();
            var exact = await EnumerateLikeTheSdkAsync(session, "q", DateTime.MaxValue, pageSize: 5, exactPages);

            // Assert
            odd.ShouldBe(new[] { "s-0", "s-1", "s-2", "s-3", "s-4" });
            oddPages.SequenceEqual(new[] { 2, 2, 1 }).ShouldBeTrue("a short final page ends the enumeration");
            exact.ShouldBe(new[] { "s-0", "s-1", "s-2", "s-3", "s-4" });
            exactPages.SequenceEqual(new[] { 5 }).ShouldBeTrue("an exact-multiple set ends on the following 204");

            await session.CloseAsync();
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    [Fact]
    public async Task GetMessageSessions_LastUpdatedTime_FiltersOnSessionStateUpdateInstant()
    {
        // Arrange
        var clock = new FakeTimeProvider();
        await using var harness = await TestListenerHarness.StartAsync(timeProvider: clock);
        await harness.Queues.CreateAsync(new QueueDescriptor { Name = "q", RequiresSession = true });
        await harness.Store.SetSessionStateAsync("q", "s-old", [1]);
        var cutoff = clock.GetUtcNow();
        clock.Advance(TimeSpan.FromMinutes(5));
        await harness.Store.SetSessionStateAsync("q", "s-new", [2]);
        await harness.Store.EnqueueAsync("q", [3], sessionId: "s-messages-only");

        var conn = await CreateClientFactory().CreateAsync(new Address(harness.AmqpUri));
        try
        {
            var session = new Session(conn);

            // Act
            var filtered = await GetMessageSessionsAsync(session, "q", cutoff.UtcDateTime, skip: 0, top: 100);
            var sentinel = await GetMessageSessionsAsync(session, "q", DateTime.MaxValue, skip: 0, top: 100);
            var noneMatch = await GetMessageSessionsAsync(session, "q", clock.GetUtcNow().UtcDateTime, skip: 0, top: 100);

            // Assert
            filtered.StatusCode.ShouldBe(200);
            filtered.SessionIds.ShouldBe(new[] { "s-new" },
                "filter mode only matches session state updated after the cutoff");
            sentinel.SessionIds.ShouldBe(new[] { "s-messages-only", "s-new", "s-old" },
                "DateTime.MaxValue is the all-live-sessions sentinel");
            noneMatch.StatusCode.ShouldBe(204);

            await session.CloseAsync();
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    [Fact]
    public async Task GetMessageSessions_MissingBodyFieldsAndZeroTop_UseContractDefaults()
    {
        // Arrange
        await using var harness = await TestListenerHarness.StartAsync();
        await harness.Queues.CreateAsync(new QueueDescriptor { Name = "q", RequiresSession = true });
        await harness.Store.EnqueueAsync("q", [1], sessionId: "s-1");
        await harness.Store.EnqueueAsync("q", [2], sessionId: "s-2");

        var conn = await CreateClientFactory().CreateAsync(new Address(harness.AmqpUri));
        try
        {
            var session = new Session(conn);

            // Act
            var noBody = await GetMessageSessionsAsync(session, "q", null, null, null);
            var zeroTop = await GetMessageSessionsAsync(session, "q", DateTime.MaxValue, skip: 0, top: 0);
            var negativeSkip = await GetMessageSessionsAsync(session, "q", DateTime.MaxValue, skip: -3, top: 100);

            // Assert
            noBody.StatusCode.ShouldBe(200);
            noBody.SessionIds.ShouldBe(new[] { "s-1", "s-2" }, "no body behaves like the sentinel with no paging");
            zeroTop.StatusCode.ShouldBe(204, "top 0 asks for an empty page");
            negativeSkip.SessionIds.ShouldBe(new[] { "s-1", "s-2" }, "negative skip clamps to 0");

            await session.CloseAsync();
        }
        finally
        {
            await conn.CloseAsync();
        }
    }
}
