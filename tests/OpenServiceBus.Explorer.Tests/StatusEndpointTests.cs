using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenServiceBus.Core.Entities;
using OpenServiceBus.Explorer.Api;
using OpenServiceBus.Testing;
using SdkStatus = Azure.Messaging.ServiceBus.Administration.EntityStatus;

namespace OpenServiceBus.Explorer.Tests;

/// <summary>
/// The Explorer's PUT /api/status against a real broker. Regression guard: the SDK's
/// EntityStatus is an extensible-enum struct, so a plain Enum.TryParse over it compiles
/// but throws "Type provided must be an Enum" at runtime, which surfaced in the UI as a
/// failed toast on every status change.
/// </summary>
public class StatusEndpointTests
{
    [Theory]
    [InlineData("SendDisabled", EntityStatus.SendDisabled)]
    [InlineData("receivedisabled", EntityStatus.ReceiveDisabled)]
    [InlineData("Disabled", EntityStatus.Disabled)]
    [InlineData("Active", EntityStatus.Active)]
    public async Task PutStatus_OnAQueue_UpdatesTheBrokerDescriptor(string requested, EntityStatus expected)
    {
        await using var broker = await OpenServiceBusTestHost.StartAsync();
        await broker.CreateQueueAsync("status-q");
        await using var factory = new WebApplicationFactory<Program>();
        using var http = factory.CreateClient();

        var response = await http.PutAsJsonAsync("/api/status", new
        {
            connectionString = broker.ConnectionString,
            kind = "queue",
            name = "status-q",
            subscription = (string?)null,
            status = requested,
        });

        response.StatusCode.ShouldBe(HttpStatusCode.OK,
            await response.Content.ReadAsStringAsync());
        (await broker.Queues.GetAsync("status-q"))!.Status.ShouldBe(expected);
    }

    [Fact]
    public async Task PutStatus_OnASubscription_UpdatesTheBrokerDescriptor()
    {
        await using var broker = await OpenServiceBusTestHost.StartAsync();
        await broker.Topics.CreateTopicAsync(new TopicDescriptor { Name = "status-t" });
        await broker.Topics.CreateSubscriptionAsync(new SubscriptionDescriptor { TopicName = "status-t", Name = "sub" });
        await using var factory = new WebApplicationFactory<Program>();
        using var http = factory.CreateClient();

        var response = await http.PutAsJsonAsync("/api/status", new
        {
            connectionString = broker.ConnectionString,
            kind = "subscription",
            name = "status-t",
            subscription = "sub",
            status = "ReceiveDisabled",
        });

        response.StatusCode.ShouldBe(HttpStatusCode.OK,
            await response.Content.ReadAsStringAsync());
        (await broker.Topics.GetSubscriptionAsync("status-t", "sub"))!.Status.ShouldBe(EntityStatus.ReceiveDisabled);
    }

    [Fact]
    public async Task PutStatus_UnknownValue_Returns400()
    {
        await using var broker = await OpenServiceBusTestHost.StartAsync();
        await broker.CreateQueueAsync("status-bad");
        await using var factory = new WebApplicationFactory<Program>();
        using var http = factory.CreateClient();

        var response = await http.PutAsJsonAsync("/api/status", new
        {
            connectionString = broker.ConnectionString,
            kind = "queue",
            name = "status-bad",
            subscription = (string?)null,
            status = "Paused",
        });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("Active")]
    [InlineData("sendDISABLED")]
    [InlineData("ReceiveDisabled")]
    [InlineData("Disabled")]
    public void TryParseStatus_KnownValues_ParseCaseInsensitively(string value)
    {
        var parsed = AdminEndpoints.TryParseStatus(value);
        parsed.ShouldNotBeNull();
        parsed.Value.ToString().ShouldBe(new SdkStatus(value).ToString(), StringCompareShould.IgnoreCase);
    }

    [Fact]
    public void TryParseStatus_UnknownOrNull_ReturnsNull()
    {
        AdminEndpoints.TryParseStatus("Paused").ShouldBeNull();
        AdminEndpoints.TryParseStatus(null).ShouldBeNull();
        AdminEndpoints.TryParseStatus("").ShouldBeNull();
    }
}
