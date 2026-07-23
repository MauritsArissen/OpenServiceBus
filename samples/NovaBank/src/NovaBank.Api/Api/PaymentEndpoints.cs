using Azure.Messaging.ServiceBus;
using NovaBank.Api.Contracts;
using NovaBank.Api.Domain;
using NovaBank.Api.Infrastructure;
using NovaBank.Api.Messaging;

namespace NovaBank.Api.Api;

public static class PaymentEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/payments").WithTags("Payments");

        group.MapPost("", async (
            CreatePaymentRequest request,
            InMemoryBankStore store,
            BusSenders bus,
            IEventPublisher events,
            TimeProvider time) =>
        {
            if (request.Amount <= 0) return Results.BadRequest(new { error = "amount must be positive." });
            if (string.IsNullOrWhiteSpace(request.PayeeName) || string.IsNullOrWhiteSpace(request.PayeeIban))
            {
                return Results.BadRequest(new { error = "payeeName and payeeIban are required." });
            }

            var account = store.GetAccount(request.AccountId);
            if (account is null) return Results.NotFound(new { error = $"Account '{request.AccountId}' not found." });
            if (account.Status == AccountStatus.Frozen)
            {
                return Results.Conflict(new { error = "source_account_frozen" });
            }
            if (!string.Equals(account.Currency, request.Currency, StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest(new { error = "currency must match the account." });
            }

            var now = time.GetUtcNow();
            var executeAt = request.ExecuteAtUtc ?? now;
            var isScheduled = executeAt > now.AddSeconds(1);

            var payment = store.AddPayment(new PaymentRecord
            {
                Id = $"PAY-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}",
                AccountId = request.AccountId,
                PayeeName = request.PayeeName.Trim(),
                PayeeIban = request.PayeeIban.Trim(),
                Amount = request.Amount,
                Currency = request.Currency.ToUpperInvariant(),
                Reference = request.Reference,
                ExecuteAtUtc = executeAt,
                Status = isScheduled ? PaymentStatus.Scheduled : PaymentStatus.Queued,
                CreatedAtUtc = now,
            });

            var instruction = new PaymentInstruction(
                payment.Id, payment.AccountId, payment.PayeeName, payment.PayeeIban,
                payment.Amount, payment.Currency, payment.Reference);
            var message = new ServiceBusMessage(BinaryData.FromObjectAsJson(instruction, BusJson.Options))
            {
                MessageId = payment.Id,
                // Session = account: the broker guarantees FIFO execution per account.
                SessionId = payment.AccountId,
                Subject = "payment.instruction",
                ContentType = "application/json",
            };

            if (isScheduled)
            {
                // The broker holds the message invisibly until the due time - surviving
                // API restarts, which an in-process timer would not.
                await bus.Payments.ScheduleMessageAsync(message, executeAt);
            }
            else
            {
                await bus.Payments.SendMessageAsync(message);
            }

            await events.PublishAsync(
                EventTypes.PaymentScheduled,
                new
                {
                    paymentId = payment.Id,
                    accountId = payment.AccountId,
                    requestedAmount = payment.Amount,
                    currency = payment.Currency,
                    executeAtUtc = executeAt,
                    scheduled = isScheduled,
                },
                accountId: payment.AccountId);

            return Results.Accepted($"/api/payments/{payment.Id}", PaymentResponse.From(payment));
        })
        .WithSummary("Create a payment (immediate or scheduled)")
        .WithDescription(
            "Payments ride a session-enabled queue keyed by account id: per-account FIFO, cross-account " +
            "concurrency. Set executeAtUtc in the future to have the broker hold the instruction until then.");

        group.MapGet("", (string? accountId, InMemoryBankStore store) =>
            Results.Ok(store.ListPayments(accountId).Select(PaymentResponse.From)))
        .WithSummary("List payments");

        group.MapGet("/{id}", (string id, InMemoryBankStore store) =>
        {
            var payment = store.GetPayment(id);
            return payment is null ? Results.NotFound() : Results.Ok(PaymentResponse.From(payment));
        })
        .WithSummary("Get a payment and its execution status");
    }
}
