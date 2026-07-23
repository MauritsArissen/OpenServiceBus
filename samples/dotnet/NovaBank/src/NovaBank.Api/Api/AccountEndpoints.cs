using NovaBank.Api.Contracts;
using NovaBank.Api.Domain;
using NovaBank.Api.Infrastructure;
using NovaBank.Api.Messaging;

namespace NovaBank.Api.Api;

public static class AccountEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/accounts").WithTags("Accounts");

        group.MapPost("", async (OpenAccountRequest request, InMemoryBankStore store, IEventPublisher events) =>
        {
            if (store.GetCustomer(request.CustomerId) is null)
            {
                return Results.NotFound(new { error = $"Customer '{request.CustomerId}' not found." });
            }
            if (string.IsNullOrWhiteSpace(request.Currency) || request.Currency.Trim().Length != 3)
            {
                return Results.BadRequest(new { error = "currency must be a 3-letter ISO code." });
            }
            if (request.OpeningBalance < 0)
            {
                return Results.BadRequest(new { error = "openingBalance cannot be negative." });
            }

            var account = store.OpenAccount(request.CustomerId, request.Currency.Trim(), request.OpeningBalance);
            // Opening balance is capital brought into the bank, not a settled movement -
            // published without the "amount" property so it never trips the fraud filter.
            await events.PublishAsync(
                EventTypes.AccountOpened,
                new { accountId = account.Id, customerId = account.CustomerId, account.Currency, openingBalance = account.Balance },
                accountId: account.Id);
            return Results.Created($"/api/accounts/{account.Id}", AccountResponse.From(account));
        })
        .WithSummary("Open an account");

        group.MapGet("", (string? customerId, InMemoryBankStore store) =>
            Results.Ok(store.ListAccounts(customerId).Select(AccountResponse.From)))
        .WithSummary("List accounts");

        group.MapGet("/{id}", (string id, InMemoryBankStore store) =>
        {
            var account = store.GetAccount(id);
            return account is null ? Results.NotFound() : Results.Ok(AccountResponse.From(account));
        })
        .WithSummary("Get an account (incl. live balance and status)");

        group.MapPost("/{id}/deposit", async (string id, MoneyRequest request, InMemoryBankStore store, IEventPublisher events) =>
        {
            if (request.Amount <= 0) return Results.BadRequest(new { error = "amount must be positive." });
            var account = store.GetAccount(id);
            if (account is null) return Results.NotFound();

            var outcome = store.Deposit(id, request.Amount);
            if (outcome != MoneyMoveOutcome.Completed)
            {
                return Results.Conflict(new { error = outcome.ToReason() });
            }

            // A settled movement: carries the amount property, so cash deposits >= 10k
            // land on the fraud desk exactly like large transfers do.
            await events.PublishAsync(
                EventTypes.AccountDeposited,
                new { accountId = id, amount = request.Amount, account.Currency },
                settledAmount: request.Amount,
                accountId: id);
            return Results.Ok(AccountResponse.From(store.GetAccount(id)!));
        })
        .WithSummary("Deposit cash");

        group.MapPost("/{id}/withdraw", async (string id, MoneyRequest request, InMemoryBankStore store, IEventPublisher events) =>
        {
            if (request.Amount <= 0) return Results.BadRequest(new { error = "amount must be positive." });
            var account = store.GetAccount(id);
            if (account is null) return Results.NotFound();

            var outcome = store.Withdraw(id, request.Amount);
            if (outcome != MoneyMoveOutcome.Completed)
            {
                return Results.Conflict(new { error = outcome.ToReason() });
            }

            await events.PublishAsync(
                EventTypes.AccountWithdrawn,
                new { accountId = id, amount = request.Amount, account.Currency },
                settledAmount: request.Amount,
                accountId: id);
            return Results.Ok(AccountResponse.From(store.GetAccount(id)!));
        })
        .WithSummary("Withdraw cash");
    }
}
