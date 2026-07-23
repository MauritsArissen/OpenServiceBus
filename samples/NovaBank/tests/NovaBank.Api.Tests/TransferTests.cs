using System.Net;
using System.Net.Http.Json;
using NovaBank.Api.Contracts;
using Shouldly;

namespace NovaBank.Api.Tests;

public class TransferTests : IClassFixture<NovaBankAppFixture>
{
    private readonly NovaBankAppFixture _app;

    public TransferTests(NovaBankAppFixture app) => _app = app;

    [Fact]
    public async Task Transfer_HappyPath_MovesMoney_AuditsAndNotifiesBothSides()
    {
        var from = await _app.OpenAccountAsync(1_000m);
        var to = await _app.OpenAccountAsync(100m);

        var transfer = await _app.PostTransferAsync(from.Id, to.Id, 250m, reference: "rent");
        // The embedded broker settles so fast the worker can finish before the 202 response
        // is even serialized - both states are legitimate here.
        transfer.Status.ShouldBeOneOf("accepted", "completed");

        var settled = await _app.WaitForSettlementAsync(transfer.Id);
        settled.Status.ShouldBe("completed");

        (await _app.GetAccountAsync(from.Id)).Balance.ShouldBe(750m);
        (await _app.GetAccountAsync(to.Id)).Balance.ShouldBe(350m);

        // Audit trail (fed by the match-all subscription) sees request + completion.
        await Eventually.SatisfiesAsync(async () =>
        {
            var audit = (await _app.Client.GetFromJsonAsync<List<AuditEntryResponse>>("/api/audit"))!;
            return audit.Any(a => a.EventType == "transfer.requested" && a.PayloadJson.Contains(transfer.Id)) &&
                   audit.Any(a => a.EventType == "transfer.completed" && a.PayloadJson.Contains(transfer.Id));
        }, because: "audit subscription should record request and completion");

        // Both customers get a notification (filtered subscription).
        await Eventually.SatisfiesAsync(async () =>
        {
            var sender = await _app.Client.GetFromJsonAsync<List<NotificationResponse>>(
                $"/api/customers/{from.CustomerId}/notifications");
            var receiver = await _app.Client.GetFromJsonAsync<List<NotificationResponse>>(
                $"/api/customers/{to.CustomerId}/notifications");
            return sender!.Any(n => n.Title == "Transfer sent") &&
                   receiver!.Any(n => n.Title == "Money received");
        }, because: "notification subscription should notify both parties");
    }

    [Fact]
    public async Task Transfer_DuplicateIdempotencyKey_ExecutesExactlyOnce()
    {
        var from = await _app.OpenAccountAsync(1_000m);
        var to = await _app.OpenAccountAsync(0m);
        var key = Guid.NewGuid().ToString("N");

        // Two identical requests - e.g. a client retry after a lost HTTP response. The API
        // re-sends the command both times; the queue's duplicate detection (MessageId = key)
        // must collapse them into a single execution.
        var first = await _app.PostTransferAsync(from.Id, to.Id, 100m, idempotencyKey: key);
        var second = await _app.PostTransferAsync(from.Id, to.Id, 100m, idempotencyKey: key);
        second.Id.ShouldBe(first.Id);

        var settled = await _app.WaitForSettlementAsync(first.Id);
        settled.Status.ShouldBe("completed");

        // Give a would-be duplicate execution time to happen, then prove it didn't.
        await Task.Delay(750);
        (await _app.GetAccountAsync(from.Id)).Balance.ShouldBe(900m);
        (await _app.GetAccountAsync(to.Id)).Balance.ShouldBe(100m);

        var audit = await _app.Client.GetFromJsonAsync<List<AuditEntryResponse>>("/api/audit?eventType=transfer.completed");
        audit!.Count(a => a.PayloadJson.Contains(first.Id)).ShouldBe(1);
    }

    [Fact]
    public async Task Transfer_InsufficientFunds_FailsAndNotifies()
    {
        var from = await _app.OpenAccountAsync(50m);
        var to = await _app.OpenAccountAsync(0m);

        var transfer = await _app.PostTransferAsync(from.Id, to.Id, 200m);
        var settled = await _app.WaitForSettlementAsync(transfer.Id);

        settled.Status.ShouldBe("failed");
        settled.FailureReason.ShouldBe("insufficient_funds");
        (await _app.GetAccountAsync(from.Id)).Balance.ShouldBe(50m);
        (await _app.GetAccountAsync(to.Id)).Balance.ShouldBe(0m);

        await Eventually.SatisfiesAsync(async () =>
        {
            var inbox = await _app.Client.GetFromJsonAsync<List<NotificationResponse>>(
                $"/api/customers/{from.CustomerId}/notifications");
            return inbox!.Any(n => n.Title == "Transfer failed" && n.Message.Contains("insufficient_funds"));
        }, because: "the customer should hear about the failed transfer");
    }

    [Fact]
    public async Task Transfer_CurrencyMismatch_IsRejectedSynchronously()
    {
        var from = await _app.OpenAccountAsync(1_000m, currency: "EUR");
        var to = await _app.OpenAccountAsync(0m, currency: "USD");

        var response = await _app.Client.PostAsJsonAsync("/api/transfers",
            new CreateTransferRequest(from.Id, to.Id, 100m, "EUR", null));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
