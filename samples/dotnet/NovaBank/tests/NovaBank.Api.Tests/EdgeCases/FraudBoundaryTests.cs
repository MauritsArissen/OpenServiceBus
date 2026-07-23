using System.Net.Http.Json;
using NovaBank.Api.Contracts;
using Shouldly;

namespace NovaBank.Api.Tests.EdgeCases;

/// <summary>
/// The fraud thresholds live in TWO places: the review threshold (10 000) is a broker-side
/// SQL filter, the freeze threshold (25 000) is worker logic. These tests pin the exact
/// boundary semantics of both, one cent either side.
/// </summary>
public class FraudBoundaryTests : IClassFixture<NovaBankAppFixture>
{
    private readonly NovaBankAppFixture _app;

    public FraudBoundaryTests(NovaBankAppFixture app) => _app = app;

    private async Task<List<FraudAlertResponse>> AlertsForAsync(string accountId) =>
        (await _app.Client.GetFromJsonAsync<List<FraudAlertResponse>>($"/api/fraud/alerts?accountId={accountId}"))!;

    private async Task<string> CompletedTransferFromAsync(decimal balance, decimal amount)
    {
        var from = await _app.OpenAccountAsync(balance);
        var to = await _app.OpenAccountAsync(0m);
        var transfer = await _app.PostTransferAsync(from.Id, to.Id, amount);
        (await _app.WaitForSettlementAsync(transfer.Id)).Status.ShouldBe("completed");
        return from.Id;
    }

    [Fact]
    public async Task OneCentBelowReviewThreshold_ProducesNoAlert()
    {
        var accountId = await CompletedTransferFromAsync(20_000m, 9_999.99m);
        await Task.Delay(750); // give a would-be alert time to arrive, then prove it didn't
        (await AlertsForAsync(accountId)).ShouldBeEmpty();
    }

    [Fact]
    public async Task ExactlyReviewThreshold_ProducesReviewAlert_NoFreeze()
    {
        var accountId = await CompletedTransferFromAsync(20_000m, 10_000m);
        await Eventually.SatisfiesAsync(
            async () => (await AlertsForAsync(accountId)).Any(a => a.Severity == "review" && a.Amount == 10_000m),
            because: "the SQL filter is amount >= 10000 - inclusive");
        (await _app.GetAccountAsync(accountId)).Status.ShouldBe("active");
    }

    [Fact]
    public async Task OneCentBelowFreezeThreshold_ReviewOnly()
    {
        var accountId = await CompletedTransferFromAsync(30_000m, 24_999.99m);
        await Eventually.SatisfiesAsync(
            async () => (await AlertsForAsync(accountId)).Any(a => a.Severity == "review"),
            because: "24_999.99 is over review but under freeze");
        (await _app.GetAccountAsync(accountId)).Status.ShouldBe("active");
    }

    [Fact]
    public async Task ExactlyFreezeThreshold_FreezesTheAccount()
    {
        var accountId = await CompletedTransferFromAsync(30_000m, 25_000m);
        await Eventually.SatisfiesAsync(
            async () => (await AlertsForAsync(accountId)).Any(a => a.Severity == "critical" && a.AccountFrozen),
            because: "the freeze threshold is inclusive");
        await Eventually.SatisfiesAsync(
            async () => (await _app.GetAccountAsync(accountId)).Status == "frozen");
    }

    [Fact]
    public async Task LargeWithdrawal_AlsoReachesTheFraudDesk()
    {
        // account.withdrawn is a settled movement and carries the amount property too.
        var account = await _app.OpenAccountAsync(15_000m);
        (await _app.Client.PostAsJsonAsync($"/api/accounts/{account.Id}/withdraw", new MoneyRequest(11_000m)))
            .EnsureSuccessStatusCode();

        await Eventually.SatisfiesAsync(
            async () => (await AlertsForAsync(account.Id)).Any(a => a.EventType == "account.withdrawn" && a.Severity == "review"),
            because: "large cash withdrawals are classic fraud-desk material");
    }
}
