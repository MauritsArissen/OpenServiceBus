using System.Net.Http.Json;
using NovaBank.Api.Contracts;
using Shouldly;

namespace NovaBank.Api.Tests.EdgeCases;

/// <summary>Boundary values for money movement: exact balances, one-cent amounts,
/// decimal precision, and very large sums.</summary>
public class MoneyEdgeTests : IClassFixture<NovaBankAppFixture>
{
    private readonly NovaBankAppFixture _app;

    public MoneyEdgeTests(NovaBankAppFixture app) => _app = app;

    [Fact]
    public async Task Transfer_OfExactBalance_SucceedsAndLeavesZero()
    {
        var from = await _app.OpenAccountAsync(123.45m);
        var to = await _app.OpenAccountAsync(0m);

        var transfer = await _app.PostTransferAsync(from.Id, to.Id, 123.45m);
        (await _app.WaitForSettlementAsync(transfer.Id)).Status.ShouldBe("completed");

        (await _app.GetAccountAsync(from.Id)).Balance.ShouldBe(0m);
        (await _app.GetAccountAsync(to.Id)).Balance.ShouldBe(123.45m);
    }

    [Fact]
    public async Task Transfer_OneCentMoreThanBalance_Fails()
    {
        var from = await _app.OpenAccountAsync(100m);
        var to = await _app.OpenAccountAsync(0m);

        var transfer = await _app.PostTransferAsync(from.Id, to.Id, 100.01m);
        var settled = await _app.WaitForSettlementAsync(transfer.Id);

        settled.Status.ShouldBe("failed");
        settled.FailureReason.ShouldBe("insufficient_funds");
        (await _app.GetAccountAsync(from.Id)).Balance.ShouldBe(100m);
    }

    [Fact]
    public async Task Transfer_OfOneCent_Works()
    {
        var from = await _app.OpenAccountAsync(0.01m);
        var to = await _app.OpenAccountAsync(0m);

        var transfer = await _app.PostTransferAsync(from.Id, to.Id, 0.01m);
        (await _app.WaitForSettlementAsync(transfer.Id)).Status.ShouldBe("completed");

        (await _app.GetAccountAsync(from.Id)).Balance.ShouldBe(0m);
        (await _app.GetAccountAsync(to.Id)).Balance.ShouldBe(0.01m);
    }

    [Fact]
    public async Task RepeatedSmallDeposits_HaveNoFloatingPointDrift()
    {
        // decimal end to end: 0.1 + 0.1 + 0.1 must be exactly 0.3, not 0.30000000000000004.
        var account = await _app.OpenAccountAsync(0m);
        for (var i = 0; i < 3; i++)
        {
            (await _app.Client.PostAsJsonAsync($"/api/accounts/{account.Id}/deposit", new MoneyRequest(0.1m)))
                .EnsureSuccessStatusCode();
        }

        (await _app.GetAccountAsync(account.Id)).Balance.ShouldBe(0.3m);

        var to = await _app.OpenAccountAsync(0m);
        var transfer = await _app.PostTransferAsync(account.Id, to.Id, 0.3m);
        (await _app.WaitForSettlementAsync(transfer.Id)).Status.ShouldBe("completed");
        (await _app.GetAccountAsync(to.Id)).Balance.ShouldBe(0.3m);
    }

    [Fact]
    public async Task VeryLargeTransfer_KeepsExactAmounts()
    {
        // Also exercises the fraud pipeline with a huge value (freezes the source afterwards -
        // post-facto detection must not affect the settled balances).
        var from = await _app.OpenAccountAsync(2_000_000_000.99m);
        var to = await _app.OpenAccountAsync(0m);

        var transfer = await _app.PostTransferAsync(from.Id, to.Id, 1_999_999_999.99m);
        (await _app.WaitForSettlementAsync(transfer.Id)).Status.ShouldBe("completed");

        (await _app.GetAccountAsync(from.Id)).Balance.ShouldBe(1.00m);
        (await _app.GetAccountAsync(to.Id)).Balance.ShouldBe(1_999_999_999.99m);

        await Eventually.SatisfiesAsync(
            async () => (await _app.GetAccountAsync(from.Id)).Status == "frozen",
            because: "a two-billion transfer should certainly trip the freeze threshold");
    }
}
