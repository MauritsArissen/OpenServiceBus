using NovaBank.Api.Contracts;
using NovaBank.Api.Infrastructure;
using NovaBank.Api.Messaging;

namespace NovaBank.Api.Api;

public static class CustomerEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/customers").WithTags("Customers");

        group.MapPost("", async (CreateCustomerRequest request, InMemoryBankStore store, IEventPublisher events) =>
        {
            if (string.IsNullOrWhiteSpace(request.FullName) || string.IsNullOrWhiteSpace(request.Email))
            {
                return Results.BadRequest(new { error = "fullName and email are required." });
            }

            var customer = store.CreateCustomer(request.FullName.Trim(), request.Email.Trim());
            await events.PublishAsync(EventTypes.CustomerCreated, new { customerId = customer.Id, customer.FullName });
            return Results.Created($"/api/customers/{customer.Id}", CustomerResponse.From(customer));
        })
        .WithSummary("Create a customer");

        group.MapGet("", (InMemoryBankStore store) =>
            Results.Ok(store.ListCustomers().Select(CustomerResponse.From)))
        .WithSummary("List customers");

        group.MapGet("/{id}", (string id, InMemoryBankStore store) =>
        {
            var customer = store.GetCustomer(id);
            return customer is null ? Results.NotFound() : Results.Ok(CustomerResponse.From(customer));
        })
        .WithSummary("Get a customer");

        group.MapGet("/{id}/notifications", (string id, InMemoryBankStore store) =>
            store.GetCustomer(id) is null
                ? Results.NotFound()
                : Results.Ok(store.ListNotifications(id).Select(NotificationResponse.From)))
        .WithSummary("Customer notification inbox")
        .WithDescription("Populated asynchronously by the notifications subscription on the events topic.");
    }
}
