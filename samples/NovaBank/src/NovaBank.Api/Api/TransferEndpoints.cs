using Azure.Messaging.ServiceBus;
using NovaBank.Api.Contracts;
using NovaBank.Api.Domain;
using NovaBank.Api.Infrastructure;
using NovaBank.Api.Messaging;

namespace NovaBank.Api.Api;

public static class TransferEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/transfers").WithTags("Transfers");

        group.MapPost("", async (
            CreateTransferRequest request,
            HttpRequest http,
            InMemoryBankStore store,
            BusSenders bus,
            IEventPublisher events,
            TimeProvider time) =>
        {
            if (request.Amount <= 0) return Results.BadRequest(new { error = "amount must be positive." });
            if (request.FromAccountId == request.ToAccountId)
            {
                return Results.BadRequest(new { error = "fromAccountId and toAccountId must differ." });
            }

            var from = store.GetAccount(request.FromAccountId);
            if (from is null) return Results.NotFound(new { error = $"Account '{request.FromAccountId}' not found." });
            var to = store.GetAccount(request.ToAccountId);
            if (to is null) return Results.NotFound(new { error = $"Account '{request.ToAccountId}' not found." });

            if (!string.Equals(from.Currency, request.Currency, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(to.Currency, request.Currency, StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest(new { error = "currency must match both accounts." });
            }
            if (from.Status == AccountStatus.Frozen)
            {
                return Results.Conflict(new { error = "source_account_frozen" });
            }

            // The Idempotency-Key header keys both the API-side record AND the broker-side
            // duplicate detection (it becomes the command's MessageId). A client retry hits
            // the same record here, and even the re-sent command is silently dropped by the
            // broker's dedup window - the transfer executes at most once.
            var idempotencyKey = http.Headers.TryGetValue("Idempotency-Key", out var header) &&
                                 !string.IsNullOrWhiteSpace(header.ToString())
                ? header.ToString()
                : Guid.NewGuid().ToString("N");

            var (transfer, created) = store.GetOrCreateTransfer(idempotencyKey, () => new TransferRecord
            {
                Id = $"TRF-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}",
                IdempotencyKey = idempotencyKey,
                FromAccountId = request.FromAccountId,
                ToAccountId = request.ToAccountId,
                Amount = request.Amount,
                Currency = request.Currency.ToUpperInvariant(),
                Reference = request.Reference,
                CreatedAtUtc = time.GetUtcNow(),
            });

            // Send unconditionally, retries included - at-least-once on our side,
            // at-most-once execution thanks to duplicate detection on the queue.
            var command = new TransferCommand(
                transfer.Id, transfer.FromAccountId, transfer.ToAccountId,
                transfer.Amount, transfer.Currency, transfer.Reference);
            var message = new ServiceBusMessage(BinaryData.FromObjectAsJson(command, BusJson.Options))
            {
                MessageId = idempotencyKey,
                Subject = "transfer.command",
                ContentType = "application/json",
            };
            message.ApplicationProperties["transferId"] = transfer.Id;
            await bus.Transfers.SendMessageAsync(message);

            if (created)
            {
                await events.PublishAsync(
                    EventTypes.TransferRequested,
                    new
                    {
                        transferId = transfer.Id,
                        fromAccountId = transfer.FromAccountId,
                        toAccountId = transfer.ToAccountId,
                        requestedAmount = transfer.Amount,
                        currency = transfer.Currency,
                        reference = transfer.Reference,
                    },
                    accountId: transfer.FromAccountId);
            }

            return created
                ? Results.Accepted($"/api/transfers/{transfer.Id}", TransferResponse.From(transfer))
                : Results.Ok(TransferResponse.From(transfer));
        })
        .WithSummary("Initiate a transfer (asynchronous)")
        .WithDescription(
            "Returns 202 immediately; a queue worker settles the transfer. Poll GET /api/transfers/{id} " +
            "for the outcome. Send an Idempotency-Key header to make retries safe. " +
            "Set reference=CHAOS to simulate a poisoned message that ends up on the dead-letter queue.");

        group.MapGet("", (InMemoryBankStore store) =>
            Results.Ok(store.ListTransfers().Select(TransferResponse.From)))
        .WithSummary("List transfers");

        group.MapGet("/{id}", (string id, InMemoryBankStore store) =>
        {
            var transfer = store.GetTransfer(id);
            return transfer is null ? Results.NotFound() : Results.Ok(TransferResponse.From(transfer));
        })
        .WithSummary("Get a transfer and its settlement status");
    }
}
