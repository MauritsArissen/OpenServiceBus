using NovaBank.Api.Domain;

namespace NovaBank.Api.Infrastructure;

/// <summary>Deterministic demo data so the Swagger UI is instantly usable.</summary>
public static class SeedData
{
    public static void Apply(InMemoryBankStore store, TimeProvider time)
    {
        var now = time.GetUtcNow();

        store.AddCustomer(new Customer { Id = "CUS-ALICE", FullName = "Alice Janssen", Email = "alice@example.com", CreatedAtUtc = now });
        store.AddCustomer(new Customer { Id = "CUS-BOB", FullName = "Bob de Vries", Email = "bob@example.com", CreatedAtUtc = now });
        store.AddCustomer(new Customer { Id = "CUS-ACME", FullName = "ACME Logistics B.V.", Email = "finance@acme.example.com", CreatedAtUtc = now });

        store.AddAccount(new Account { Id = "ACC-ALICE", CustomerId = "CUS-ALICE", Currency = "EUR", Balance = 12_500m, OpenedAtUtc = now });
        store.AddAccount(new Account { Id = "ACC-BOB", CustomerId = "CUS-BOB", Currency = "EUR", Balance = 850m, OpenedAtUtc = now });
        store.AddAccount(new Account { Id = "ACC-ACME", CustomerId = "CUS-ACME", Currency = "EUR", Balance = 250_000m, OpenedAtUtc = now });
    }
}
