using System.Net;
using System.Net.Http.Json;
using NovaBank.Api.Contracts;
using Shouldly;

namespace NovaBank.Api.Tests.EdgeCases;

/// <summary>
/// The nasty ones: retries with conflicting payloads, parallel retries hammering the same
/// idempotency key, and concurrent transfers racing to overdraw an account.
/// </summary>
public class IdempotencyAndConcurrencyTests : IClassFixture<NovaBankAppFixture>
{
    private readonly NovaBankAppFixture _app;

    public IdempotencyAndConcurrencyTests(NovaBankAppFixture app) => _app = app;

    private async Task<HttpResponseMessage> PostTransferRawAsync(
        string from, string to, decimal amount, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/transfers")
        {
            Content = JsonContent.Create(new CreateTransferRequest(from, to, amount, "EUR", null)),
        };
        request.Headers.Add("Idempotency-Key", key);
        return await _app.Client.SendAsync(request);
    }

    [Fact]
    public async Task SameKey_WithDifferentPayload_ReturnsTheOriginalTransfer()
    {
        var from = await _app.OpenAccountAsync(1_000m);
        var to = await _app.OpenAccountAsync(0m);
        var key = Guid.NewGuid().ToString("N");

        var first = await PostTransferRawAsync(from.Id, to.Id, 100m, key);
        first.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var original = (await first.Content.ReadFromJsonAsync<TransferResponse>())!;

        // A retry with a DIFFERENT amount but the same key: first write wins, no second execution.
        var second = await PostTransferRawAsync(from.Id, to.Id, 999m, key);
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        var replayed = (await second.Content.ReadFromJsonAsync<TransferResponse>())!;

        replayed.Id.ShouldBe(original.Id);
        replayed.Amount.ShouldBe(100m, "the original request's payload is authoritative");

        (await _app.WaitForSettlementAsync(original.Id)).Status.ShouldBe("completed");
        await Task.Delay(500);
        (await _app.GetAccountAsync(from.Id)).Balance.ShouldBe(900m);
    }

    [Fact]
    public async Task TenParallelPosts_WithTheSameKey_ExecuteExactlyOnce()
    {
        var from = await _app.OpenAccountAsync(1_000m);
        var to = await _app.OpenAccountAsync(0m);
        var key = Guid.NewGuid().ToString("N");

        var responses = await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(_ => PostTransferRawAsync(from.Id, to.Id, 100m, key)));

        // Exactly one caller created the record (202); the rest replayed it (200). All ten
        // sent the command; the queue's duplicate detection collapses them.
        responses.Count(r => r.StatusCode == HttpStatusCode.Accepted).ShouldBe(1);
        responses.Count(r => r.StatusCode == HttpStatusCode.OK).ShouldBe(9);

        var bodies = await Task.WhenAll(responses.Select(r => r.Content.ReadFromJsonAsync<TransferResponse>()));
        bodies.Select(b => b!.Id).Distinct().Count().ShouldBe(1);

        (await _app.WaitForSettlementAsync(bodies[0]!.Id)).Status.ShouldBe("completed");
        await Task.Delay(750);
        (await _app.GetAccountAsync(from.Id)).Balance.ShouldBe(900m);
        (await _app.GetAccountAsync(to.Id)).Balance.ShouldBe(100m);
    }

    [Fact]
    public async Task ParallelTransfers_CanNeverOverdrawTheAccount()
    {
        // Five concurrent transfers of 30 from a balance of 100: exactly three fit.
        var from = await _app.OpenAccountAsync(100m);
        var to = await _app.OpenAccountAsync(0m);

        var transfers = new List<TransferResponse>();
        foreach (var response in await Task.WhenAll(Enumerable.Range(0, 5)
            .Select(_ => PostTransferRawAsync(from.Id, to.Id, 30m, Guid.NewGuid().ToString("N")))))
        {
            response.EnsureSuccessStatusCode();
            transfers.Add((await response.Content.ReadFromJsonAsync<TransferResponse>())!);
        }

        var settled = new List<TransferResponse>();
        foreach (var transfer in transfers)
        {
            settled.Add(await _app.WaitForSettlementAsync(transfer.Id));
        }

        settled.Count(t => t.Status == "completed").ShouldBe(3);
        settled.Count(t => t.Status == "failed" && t.FailureReason == "insufficient_funds").ShouldBe(2);
        (await _app.GetAccountAsync(from.Id)).Balance.ShouldBe(10m);
        (await _app.GetAccountAsync(to.Id)).Balance.ShouldBe(90m);
    }

    [Fact]
    public async Task ParallelDeposits_AllApply()
    {
        var account = await _app.OpenAccountAsync(0m);

        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ =>
            _app.Client.PostAsJsonAsync($"/api/accounts/{account.Id}/deposit", new MoneyRequest(5m))));
        foreach (var response in responses) response.EnsureSuccessStatusCode();

        (await _app.GetAccountAsync(account.Id)).Balance.ShouldBe(50m);
    }
}
