using System.Net;
using System.Net.Http.Json;
using NovaBank.Api.Contracts;
using Shouldly;

namespace NovaBank.Api.Tests.EdgeCases;

public class PaymentEdgeTests : IClassFixture<NovaBankAppFixture>
{
    private readonly NovaBankAppFixture _app;

    public PaymentEdgeTests(NovaBankAppFixture app) => _app = app;

    [Fact]
    public async Task ExecuteAtInThePast_IsTreatedAsImmediate()
    {
        var account = await _app.OpenAccountAsync(200m);
        var response = await _app.Client.PostAsJsonAsync("/api/payments", new CreatePaymentRequest(
            account.Id, "Payee", "NL91ABNA0417164300", 50m, "EUR", null,
            ExecuteAtUtc: DateTimeOffset.UtcNow.AddMinutes(-10)));
        response.EnsureSuccessStatusCode();
        var payment = (await response.Content.ReadFromJsonAsync<PaymentResponse>())!;

        payment.Status.ShouldBe("queued", "a past due-time must not be scheduled on the broker");
        await Eventually.SatisfiesAsync(async () =>
            (await _app.Client.GetFromJsonAsync<PaymentResponse>($"/api/payments/{payment.Id}"))!.Status == "executed");
        (await _app.GetAccountAsync(account.Id)).Balance.ShouldBe(150m);
    }

    [Fact]
    public async Task Payment_OfExactBalance_ExecutesToZero()
    {
        var account = await _app.OpenAccountAsync(77.77m);
        var response = await _app.Client.PostAsJsonAsync("/api/payments", new CreatePaymentRequest(
            account.Id, "Payee", "NL91ABNA0417164300", 77.77m, "EUR", null, null));
        response.EnsureSuccessStatusCode();
        var payment = (await response.Content.ReadFromJsonAsync<PaymentResponse>())!;

        await Eventually.SatisfiesAsync(async () =>
            (await _app.Client.GetFromJsonAsync<PaymentResponse>($"/api/payments/{payment.Id}"))!.Status == "executed");
        (await _app.GetAccountAsync(account.Id)).Balance.ShouldBe(0m);
    }

    [Theory]
    [InlineData("", "NL91ABNA0417164300")]
    [InlineData("Payee", "")]
    [InlineData("   ", "NL91ABNA0417164300")]
    public async Task MissingPayeeDetails_AreRejected(string payeeName, string payeeIban)
    {
        var account = await _app.OpenAccountAsync(100m);
        var response = await _app.Client.PostAsJsonAsync("/api/payments",
            new CreatePaymentRequest(account.Id, payeeName, payeeIban, 10m, "EUR", null, null));
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CurrencyMismatch_IsRejected()
    {
        var account = await _app.OpenAccountAsync(100m, currency: "USD");
        var response = await _app.Client.PostAsJsonAsync("/api/payments",
            new CreatePaymentRequest(account.Id, "Payee", "NL91ABNA0417164300", 10m, "EUR", null, null));
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task NonPositiveAmount_IsRejected(decimal amount)
    {
        var account = await _app.OpenAccountAsync(100m);
        var response = await _app.Client.PostAsJsonAsync("/api/payments",
            new CreatePaymentRequest(account.Id, "Payee", "NL91ABNA0417164300", amount, "EUR", null, null));
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Payment_ForUnknownAccount_Returns404()
    {
        var response = await _app.Client.PostAsJsonAsync("/api/payments",
            new CreatePaymentRequest("ACC-GHOST", "Payee", "NL91ABNA0417164300", 10m, "EUR", null, null));
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
