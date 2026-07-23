using System.Net;
using System.Net.Http.Json;
using NovaBank.Api.Contracts;
using Shouldly;

namespace NovaBank.Api.Tests.EdgeCases;

/// <summary>
/// A frozen account must block every money path. Freezing happens through the real
/// pipeline: a 25k cash deposit trips the fraud subscription's freeze threshold.
/// </summary>
public class FrozenAccountTests : IClassFixture<NovaBankAppFixture>
{
    private readonly NovaBankAppFixture _app;

    public FrozenAccountTests(NovaBankAppFixture app) => _app = app;

    private async Task<AccountResponse> OpenFrozenAccountAsync()
    {
        var account = await _app.OpenAccountAsync(0m);
        (await _app.Client.PostAsJsonAsync($"/api/accounts/{account.Id}/deposit", new MoneyRequest(25_000m)))
            .EnsureSuccessStatusCode();
        await Eventually.SatisfiesAsync(
            async () => (await _app.GetAccountAsync(account.Id)).Status == "frozen",
            because: "a 25k cash deposit crosses the freeze threshold");
        return await _app.GetAccountAsync(account.Id);
    }

    [Fact]
    public async Task LargeCashDeposit_FreezesTheAccount_ViaTheFraudPipeline()
    {
        var frozen = await OpenFrozenAccountAsync();
        frozen.Status.ShouldBe("frozen");
        frozen.Balance.ShouldBe(25_000m, "the deposit itself settled before detection - freeze is post-facto");

        var alerts = await _app.Client.GetFromJsonAsync<List<FraudAlertResponse>>(
            $"/api/fraud/alerts?accountId={frozen.Id}");
        alerts!.ShouldContain(a => a.Severity == "critical" && a.EventType == "account.deposited");
    }

    [Fact]
    public async Task TransferInto_FrozenDestination_FailsAsynchronously()
    {
        var frozen = await OpenFrozenAccountAsync();
        var from = await _app.OpenAccountAsync(500m);

        // The API only vets the SOURCE synchronously; a frozen destination is the worker's
        // finding, so the transfer is accepted and then fails at settlement.
        var transfer = await _app.PostTransferAsync(from.Id, frozen.Id, 100m);
        var settled = await _app.WaitForSettlementAsync(transfer.Id);

        settled.Status.ShouldBe("failed");
        settled.FailureReason.ShouldBe("destination_account_frozen");
        (await _app.GetAccountAsync(from.Id)).Balance.ShouldBe(500m);
    }

    [Fact]
    public async Task DepositAndWithdraw_OnFrozenAccount_Return409()
    {
        var frozen = await OpenFrozenAccountAsync();

        var deposit = await _app.Client.PostAsJsonAsync($"/api/accounts/{frozen.Id}/deposit", new MoneyRequest(10m));
        deposit.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await deposit.Content.ReadAsStringAsync()).ShouldContain("destination_account_frozen");

        var withdraw = await _app.Client.PostAsJsonAsync($"/api/accounts/{frozen.Id}/withdraw", new MoneyRequest(10m));
        withdraw.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await withdraw.Content.ReadAsStringAsync()).ShouldContain("source_account_frozen");

        (await _app.GetAccountAsync(frozen.Id)).Balance.ShouldBe(25_000m);
    }

    [Fact]
    public async Task Payment_FromFrozenAccount_IsRejectedSynchronously()
    {
        var frozen = await OpenFrozenAccountAsync();

        var response = await _app.Client.PostAsJsonAsync("/api/payments",
            new CreatePaymentRequest(frozen.Id, "Payee", "NL91ABNA0417164300", 10m, "EUR", null, null));
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }
}
