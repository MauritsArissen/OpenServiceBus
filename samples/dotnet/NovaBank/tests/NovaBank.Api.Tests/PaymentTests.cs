using System.Net.Http.Json;
using NovaBank.Api.Contracts;
using Shouldly;

namespace NovaBank.Api.Tests;

/// <summary>
/// Payments ride a session-enabled queue (SessionId = accountId) and can be scheduled on
/// the broker. These tests cover immediate execution, broker-side scheduling, and
/// per-account FIFO ordering.
/// </summary>
public class PaymentTests : IClassFixture<NovaBankAppFixture>
{
    private readonly NovaBankAppFixture _app;

    public PaymentTests(NovaBankAppFixture app) => _app = app;

    private async Task<PaymentResponse> PostPaymentAsync(
        string accountId, decimal amount, DateTimeOffset? executeAtUtc = null, string? reference = null)
    {
        var response = await _app.Client.PostAsJsonAsync("/api/payments",
            new CreatePaymentRequest(accountId, "Energy Co", "NL91ABNA0417164300", amount, "EUR", reference, executeAtUtc));
        response.IsSuccessStatusCode.ShouldBeTrue(
            $"POST /api/payments returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<PaymentResponse>())!;
    }

    private Task<PaymentResponse> GetPaymentAsync(string id) =>
        _app.Client.GetFromJsonAsync<PaymentResponse>($"/api/payments/{id}")!;

    [Fact]
    public async Task ImmediatePayment_Executes()
    {
        var account = await _app.OpenAccountAsync(500m);

        var payment = await PostPaymentAsync(account.Id, 120m);
        payment.Status.ShouldBe("queued");

        await Eventually.SatisfiesAsync(
            async () => (await GetPaymentAsync(payment.Id)).Status == "executed",
            because: "the payment worker should pick the instruction up");

        (await _app.GetAccountAsync(account.Id)).Balance.ShouldBe(380m);

        await Eventually.SatisfiesAsync(async () =>
        {
            var inbox = await _app.Client.GetFromJsonAsync<List<NotificationResponse>>(
                $"/api/customers/{account.CustomerId}/notifications");
            return inbox!.Any(n => n.Title == "Payment executed");
        }, because: "payment.executed should reach the notifications subscription");
    }

    [Fact]
    public async Task ScheduledPayment_IsHeldByTheBroker_ThenExecutes()
    {
        var account = await _app.OpenAccountAsync(500m);
        var executeAt = DateTimeOffset.UtcNow.AddSeconds(2);

        var payment = await PostPaymentAsync(account.Id, 75m, executeAtUtc: executeAt);
        payment.Status.ShouldBe("scheduled");

        // Before the due time the broker holds the message - nothing may execute.
        (await GetPaymentAsync(payment.Id)).Status.ShouldBe("scheduled");

        await Eventually.SatisfiesAsync(
            async () => (await GetPaymentAsync(payment.Id)).Status == "executed",
            because: "the broker should release the scheduled message at the due time");

        var executed = await GetPaymentAsync(payment.Id);
        executed.ExecutedAtUtc!.Value.ShouldBeGreaterThanOrEqualTo(executeAt.AddMilliseconds(-500));
        (await _app.GetAccountAsync(account.Id)).Balance.ShouldBe(425m);
    }

    [Fact]
    public async Task PaymentsForOneAccount_ExecuteInSubmissionOrder()
    {
        var account = await _app.OpenAccountAsync(1_000m);

        // Same account => same session => the broker guarantees FIFO.
        var p1 = await PostPaymentAsync(account.Id, 10m, reference: "first");
        var p2 = await PostPaymentAsync(account.Id, 20m, reference: "second");
        var p3 = await PostPaymentAsync(account.Id, 30m, reference: "third");

        await Eventually.SatisfiesAsync(async () =>
        {
            var payments = await Task.WhenAll(GetPaymentAsync(p1.Id), GetPaymentAsync(p2.Id), GetPaymentAsync(p3.Id));
            return payments.All(p => p.Status == "executed");
        }, because: "all three payments should execute");

        var (e1, e2, e3) = (await GetPaymentAsync(p1.Id), await GetPaymentAsync(p2.Id), await GetPaymentAsync(p3.Id));
        e1.ExecutionOrder!.Value.ShouldBeLessThan(e2.ExecutionOrder!.Value);
        e2.ExecutionOrder!.Value.ShouldBeLessThan(e3.ExecutionOrder!.Value);

        (await _app.GetAccountAsync(account.Id)).Balance.ShouldBe(940m);
    }

    [Fact]
    public async Task Payment_WithInsufficientFunds_FailsAndNotifies()
    {
        var account = await _app.OpenAccountAsync(50m);

        var payment = await PostPaymentAsync(account.Id, 500m);

        await Eventually.SatisfiesAsync(
            async () => (await GetPaymentAsync(payment.Id)).Status == "failed",
            because: "the worker should reject a payment the balance cannot cover");

        (await GetPaymentAsync(payment.Id)).FailureReason.ShouldBe("insufficient_funds");
        (await _app.GetAccountAsync(account.Id)).Balance.ShouldBe(50m);
    }
}
