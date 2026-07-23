using System.Net.Http.Json;
using NovaBank.Api.Contracts;
using Shouldly;

namespace NovaBank.Api.Tests;

public class DeadLetterTests : IClassFixture<NovaBankAppFixture>
{
    private readonly NovaBankAppFixture _app;

    public DeadLetterTests(NovaBankAppFixture app) => _app = app;

    [Fact]
    public async Task PoisonTransfer_IsRetried_ThenDeadLettered_AndVisibleViaAdminApi()
    {
        var from = await _app.OpenAccountAsync(1_000m);
        var to = await _app.OpenAccountAsync(0m);

        // reference=CHAOS makes the worker throw on every delivery. The broker retries up
        // to MaxDeliveryCount (3 on this queue) and then moves the command to the DLQ.
        var transfer = await _app.PostTransferAsync(from.Id, to.Id, 100m, reference: "CHAOS");

        await Eventually.SatisfiesAsync(async () =>
        {
            var dlq = await _app.Client.GetFromJsonAsync<List<DeadLetterMessageResponse>>(
                "/api/admin/dead-letters/nova-transfers");
            return dlq!.Any(m => m.Body.Contains(transfer.Id));
        }, because: "the poisoned command should land on the dead-letter queue");

        // No money moved and the transfer never settled.
        (await _app.GetTransferAsync(transfer.Id)).Status.ShouldBe("accepted");
        (await _app.GetAccountAsync(from.Id)).Balance.ShouldBe(1_000m);
        (await _app.GetAccountAsync(to.Id)).Balance.ShouldBe(0m);
    }
}
