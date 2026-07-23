using System.Net;
using System.Net.Http.Json;
using NovaBank.Api.Contracts;
using Shouldly;

namespace NovaBank.Api.Tests.EdgeCases;

/// <summary>Every synchronous rejection the API can produce: bad input, unknown ids,
/// and rule violations that must never reach the service bus.</summary>
public class ApiValidationTests : IClassFixture<NovaBankAppFixture>
{
    private readonly NovaBankAppFixture _app;

    public ApiValidationTests(NovaBankAppFixture app) => _app = app;

    // ---- customers -------------------------------------------------------------------

    [Theory]
    [InlineData("", "a@b.com")]
    [InlineData("   ", "a@b.com")]
    [InlineData("Alice", "")]
    [InlineData("Alice", "   ")]
    public async Task CreateCustomer_MissingFields_IsRejected(string fullName, string email)
    {
        var response = await _app.Client.PostAsJsonAsync("/api/customers", new CreateCustomerRequest(fullName, email));
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task UnknownCustomer_Returns404_ForProfileAndNotifications()
    {
        (await _app.Client.GetAsync("/api/customers/CUS-DOES-NOT-EXIST")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _app.Client.GetAsync("/api/customers/CUS-DOES-NOT-EXIST/notifications")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // ---- accounts --------------------------------------------------------------------

    [Fact]
    public async Task OpenAccount_ForUnknownCustomer_Returns404()
    {
        var response = await _app.Client.PostAsJsonAsync("/api/accounts",
            new OpenAccountRequest("CUS-GHOST", "EUR", 100m));
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("EURO")]
    [InlineData("E")]
    [InlineData("")]
    public async Task OpenAccount_InvalidCurrency_IsRejected(string currency)
    {
        var customer = (await (await _app.Client.PostAsJsonAsync("/api/customers",
            new CreateCustomerRequest("Currency Edge", "c@e.com"))).Content.ReadFromJsonAsync<CustomerResponse>())!;
        var response = await _app.Client.PostAsJsonAsync("/api/accounts",
            new OpenAccountRequest(customer.Id, currency, 0m));
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task OpenAccount_NegativeOpeningBalance_IsRejected()
    {
        var customer = (await (await _app.Client.PostAsJsonAsync("/api/customers",
            new CreateCustomerRequest("Negative", "n@e.com"))).Content.ReadFromJsonAsync<CustomerResponse>())!;
        var response = await _app.Client.PostAsJsonAsync("/api/accounts",
            new OpenAccountRequest(customer.Id, "EUR", -1m));
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-10)]
    public async Task DepositAndWithdraw_NonPositiveAmounts_AreRejected(decimal amount)
    {
        var account = await _app.OpenAccountAsync(100m);
        (await _app.Client.PostAsJsonAsync($"/api/accounts/{account.Id}/deposit", new MoneyRequest(amount)))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await _app.Client.PostAsJsonAsync($"/api/accounts/{account.Id}/withdraw", new MoneyRequest(amount)))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Deposit_UnknownAccount_Returns404()
    {
        (await _app.Client.PostAsJsonAsync("/api/accounts/ACC-GHOST/deposit", new MoneyRequest(10m)))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Withdraw_MoreThanBalance_Returns409_AndKeepsBalance()
    {
        var account = await _app.OpenAccountAsync(20m);
        var response = await _app.Client.PostAsJsonAsync($"/api/accounts/{account.Id}/withdraw", new MoneyRequest(20.01m));
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await _app.GetAccountAsync(account.Id)).Balance.ShouldBe(20m);
    }

    // ---- transfers ---------------------------------------------------------------------

    [Fact]
    public async Task Transfer_ToUnknownAccounts_Returns404_EitherSide()
    {
        var real = await _app.OpenAccountAsync(100m);
        (await _app.Client.PostAsJsonAsync("/api/transfers",
            new CreateTransferRequest("ACC-GHOST", real.Id, 10m, "EUR", null))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _app.Client.PostAsJsonAsync("/api/transfers",
            new CreateTransferRequest(real.Id, "ACC-GHOST", 10m, "EUR", null))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Transfer_ToSameAccount_IsRejected()
    {
        var account = await _app.OpenAccountAsync(100m);
        (await _app.Client.PostAsJsonAsync("/api/transfers",
            new CreateTransferRequest(account.Id, account.Id, 10m, "EUR", null))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-50)]
    public async Task Transfer_NonPositiveAmount_IsRejected(decimal amount)
    {
        var from = await _app.OpenAccountAsync(100m);
        var to = await _app.OpenAccountAsync(0m);
        (await _app.Client.PostAsJsonAsync("/api/transfers",
            new CreateTransferRequest(from.Id, to.Id, amount, "EUR", null))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task UnknownTransferAndPayment_Return404()
    {
        (await _app.Client.GetAsync("/api/transfers/TRF-GHOST")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _app.Client.GetAsync("/api/payments/PAY-GHOST")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // ---- operations ----------------------------------------------------------------------

    [Fact]
    public async Task AdminDeadLetters_UnknownQueue_Returns404_WithAllowedList()
    {
        var response = await _app.Client.GetAsync("/api/admin/dead-letters/some-other-queue");
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync()).ShouldContain("nova-transfers");
    }

    [Fact]
    public async Task AdminDeadLetters_EmptyPaymentsDlq_ReturnsEmptyList()
    {
        var dlq = await _app.Client.GetFromJsonAsync<List<DeadLetterMessageResponse>>("/api/admin/dead-letters/nova-payments");
        dlq.ShouldNotBeNull();
        dlq!.ShouldBeEmpty();
    }
}
