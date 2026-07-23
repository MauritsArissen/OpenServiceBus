using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using NovaBank.Api.Contracts;
using OpenServiceBus.Testing;
using Shouldly;

namespace NovaBank.Api.Tests;

/// <summary>
/// One embedded OpenServiceBus broker + one in-process NovaBank API per test class.
/// The app is configured *only* through its normal configuration surface - the sole
/// difference from production is the connection string, which points at the ephemeral
/// broker instead of Azure.
/// </summary>
public sealed class NovaBankAppFixture : IAsyncLifetime
{
    public OpenServiceBusTestHost Bus { get; private set; } = null!;
    public WebApplicationFactory<Program> Factory { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Bus = await OpenServiceBusTestHost.StartAsync();
        await NovaBankTopology.CreateAsync(Bus);

        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ServiceBus:ConnectionString", Bus.ConnectionString);
            // Short SDK retry back-off: session pickup against the emulator is poll-based,
            // so the default 0.8s delay is most of a test's wall time.
            builder.UseSetting("ServiceBus:ClientRetryDelay", "00:00:00.100");
        });
        Client = Factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        Client.Dispose();
        await Factory.DisposeAsync(); // stop the processors before the broker goes away
        await Bus.DisposeAsync();
    }

    // ---- API helpers ---------------------------------------------------------------------

    public async Task<AccountResponse> OpenAccountAsync(decimal openingBalance, string currency = "EUR")
    {
        var customerResponse = await Client.PostAsJsonAsync("/api/customers",
            new CreateCustomerRequest($"Test Customer {Guid.NewGuid():N}", "test@example.com"));
        customerResponse.EnsureSuccessStatusCode();
        var customer = (await customerResponse.Content.ReadFromJsonAsync<CustomerResponse>())!;

        var accountResponse = await Client.PostAsJsonAsync("/api/accounts",
            new OpenAccountRequest(customer.Id, currency, openingBalance));
        accountResponse.EnsureSuccessStatusCode();
        return (await accountResponse.Content.ReadFromJsonAsync<AccountResponse>())!;
    }

    public async Task<AccountResponse> GetAccountAsync(string accountId) =>
        (await Client.GetFromJsonAsync<AccountResponse>($"/api/accounts/{accountId}"))!;

    public async Task<TransferResponse> PostTransferAsync(
        string fromAccountId, string toAccountId, decimal amount,
        string? idempotencyKey = null, string? reference = null, string currency = "EUR")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/transfers")
        {
            Content = JsonContent.Create(new CreateTransferRequest(fromAccountId, toAccountId, amount, currency, reference)),
        };
        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }
        var response = await Client.SendAsync(request);
        response.IsSuccessStatusCode.ShouldBeTrue(
            $"POST /api/transfers returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<TransferResponse>())!;
    }

    public async Task<TransferResponse> GetTransferAsync(string transferId) =>
        (await Client.GetFromJsonAsync<TransferResponse>($"/api/transfers/{transferId}"))!;

    /// <summary>Poll until the transfer leaves the 'accepted' state, then return it.</summary>
    public async Task<TransferResponse> WaitForSettlementAsync(string transferId)
    {
        TransferResponse? latest = null;
        await Eventually.SatisfiesAsync(async () =>
        {
            latest = await GetTransferAsync(transferId);
            return latest.Status != "accepted";
        }, because: $"transfer {transferId} should settle");
        return latest!;
    }
}

/// <summary>Polls an async condition until it holds or a timeout expires - the bus is
/// asynchronous by design, so assertions on its side effects have to wait for them.</summary>
public static class Eventually
{
    public static async Task SatisfiesAsync(
        Func<Task<bool>> condition,
        int timeoutSeconds = 15,
        string? because = null)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(100);
        }
        throw new TimeoutException(
            $"Condition not met within {timeoutSeconds}s{(because is null ? "" : $": {because}")}.");
    }
}
