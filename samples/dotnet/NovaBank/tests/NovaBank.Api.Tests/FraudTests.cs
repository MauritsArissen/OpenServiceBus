using System.Net;
using System.Net.Http.Json;
using NovaBank.Api.Contracts;
using Shouldly;

namespace NovaBank.Api.Tests;

/// <summary>
/// The fraud subscription's SQL filter (amount >= 10000) runs inside the broker - these
/// tests prove filtering, alerting, and the automated freeze loop end to end.
/// </summary>
public class FraudTests : IClassFixture<NovaBankAppFixture>
{
    private readonly NovaBankAppFixture _app;

    public FraudTests(NovaBankAppFixture app) => _app = app;

    [Fact]
    public async Task LargeTransfer_RaisesReviewAlert_WithoutFreezing()
    {
        var from = await _app.OpenAccountAsync(20_000m);
        var to = await _app.OpenAccountAsync(0m);

        var transfer = await _app.PostTransferAsync(from.Id, to.Id, 12_000m);
        (await _app.WaitForSettlementAsync(transfer.Id)).Status.ShouldBe("completed");

        await Eventually.SatisfiesAsync(async () =>
        {
            var alerts = await _app.Client.GetFromJsonAsync<List<FraudAlertResponse>>(
                $"/api/fraud/alerts?accountId={from.Id}");
            return alerts!.Any(a => a.Severity == "review" && a.Amount == 12_000m);
        }, because: "a 12k transfer crosses the review threshold");

        (await _app.GetAccountAsync(from.Id)).Status.ShouldBe("active");
    }

    [Fact]
    public async Task VeryLargeTransfer_FreezesAccount_NotifiesCustomer_AndBlocksNewTransfers()
    {
        var from = await _app.OpenAccountAsync(100_000m);
        var to = await _app.OpenAccountAsync(0m);

        var transfer = await _app.PostTransferAsync(from.Id, to.Id, 30_000m);
        (await _app.WaitForSettlementAsync(transfer.Id)).Status.ShouldBe("completed");

        await Eventually.SatisfiesAsync(async () =>
        {
            var alerts = await _app.Client.GetFromJsonAsync<List<FraudAlertResponse>>(
                $"/api/fraud/alerts?accountId={from.Id}");
            return alerts!.Any(a => a.Severity == "critical" && a.AccountFrozen);
        }, because: "a 30k transfer crosses the freeze threshold");

        await Eventually.SatisfiesAsync(
            async () => (await _app.GetAccountAsync(from.Id)).Status == "frozen",
            because: "the fraud worker should freeze the account");

        // account.frozen fans back through the topic into the notifications subscription.
        await Eventually.SatisfiesAsync(async () =>
        {
            var inbox = await _app.Client.GetFromJsonAsync<List<NotificationResponse>>(
                $"/api/customers/{from.CustomerId}/notifications");
            return inbox!.Any(n => n.Title == "Account frozen");
        }, because: "the customer should be told their account is frozen");

        // And the API now refuses new transfers from the frozen account.
        var rejected = await _app.Client.PostAsJsonAsync("/api/transfers",
            new CreateTransferRequest(from.Id, to.Id, 10m, "EUR", null));
        rejected.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task SmallTransfer_NeverReachesTheFraudDesk()
    {
        var from = await _app.OpenAccountAsync(1_000m);
        var to = await _app.OpenAccountAsync(0m);

        var transfer = await _app.PostTransferAsync(from.Id, to.Id, 500m);
        (await _app.WaitForSettlementAsync(transfer.Id)).Status.ShouldBe("completed");

        // The broker filter drops the event before it ever reaches the fraud worker.
        await Task.Delay(750);
        var alerts = await _app.Client.GetFromJsonAsync<List<FraudAlertResponse>>(
            $"/api/fraud/alerts?accountId={from.Id}");
        alerts.ShouldBeEmpty();
    }
}
