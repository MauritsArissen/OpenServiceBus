using System.Net;
using System.Text;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Microsoft.Extensions.Time.Testing;
using OpenServiceBus.Core.Entities;
using OpenServiceBus.Testing;

namespace OpenServiceBus.IntegrationTests;

/// <summary>
/// AutoDeleteOnIdle (issue #23): round-trip through the admin client, fake-time-driven
/// deletion of idle entities, activity resetting the idle clock, and the 5-minute minimum.
/// </summary>
public class AutoDeleteOnIdleTests
{
    [Fact]
    public async Task AutoDeleteOnIdle_RoundTrips_ThroughTheAdminClient()
    {
        await using var harness = await IntegrationHarness.StartAsync();
        var admin = new ServiceBusAdministrationClient(harness.ConnectionString);

        await admin.CreateQueueAsync(new CreateQueueOptions("evanescent") { AutoDeleteOnIdle = TimeSpan.FromMinutes(10) });
        QueueProperties fetched = await admin.GetQueueAsync("evanescent");
        fetched.AutoDeleteOnIdle.ShouldBe(TimeSpan.FromMinutes(10));

        fetched.AutoDeleteOnIdle = TimeSpan.FromMinutes(30);
        await admin.UpdateQueueAsync(fetched);
        ((QueueProperties)await admin.GetQueueAsync("evanescent")).AutoDeleteOnIdle.ShouldBe(TimeSpan.FromMinutes(30));

        (await harness.Queues.GetAsync("evanescent"))!.AutoDeleteOnIdle.ShouldBe(TimeSpan.FromMinutes(30));
    }

    [Fact]
    public async Task IdleQueue_IsGoneAfterTheWindow_AdminGetThrowsNotFound()
    {
        var clock = new FakeTimeProvider();
        await using var host = await OpenServiceBusTestHost.StartAsync(o => o.TimeProvider = clock);
        await host.Queues.CreateAsync(new QueueDescriptor { Name = "ephemeral", AutoDeleteOnIdle = TimeSpan.FromMinutes(10) });
        var admin = new ServiceBusAdministrationClient(host.ConnectionString);
        ((QueueProperties)await admin.GetQueueAsync("ephemeral")).Name.ShouldBe("ephemeral");

        clock.Advance(TimeSpan.FromMinutes(11));
        for (var i = 0; i < 50 && await host.Queues.GetAsync("ephemeral") is not null; i++)
        {
            await Task.Delay(100);
        }

        (await host.Queues.GetAsync("ephemeral")).ShouldBeNull("the idle window elapsed with no activity");
        var ex = await Should.ThrowAsync<ServiceBusException>(() => admin.GetQueueAsync("ephemeral"));
        ex.Reason.ShouldBe(ServiceBusFailureReason.MessagingEntityNotFound);
    }

    [Fact]
    public async Task Activity_ResetsTheIdleClock()
    {
        var clock = new FakeTimeProvider();
        await using var host = await OpenServiceBusTestHost.StartAsync(o => o.TimeProvider = clock);
        await host.Queues.CreateAsync(new QueueDescriptor { Name = "busy", AutoDeleteOnIdle = TimeSpan.FromMinutes(10) });
        await using var client = new ServiceBusClient(host.ConnectionString);

        clock.Advance(TimeSpan.FromMinutes(6));
        await client.CreateSender("busy").SendMessageAsync(new ServiceBusMessage("keepalive"));

        clock.Advance(TimeSpan.FromMinutes(6));
        await Task.Delay(400);
        (await host.Queues.GetAsync("busy")).ShouldNotBeNull("the send 6 minutes ago reset the idle clock");

        clock.Advance(TimeSpan.FromMinutes(5));
        for (var i = 0; i < 50 && await host.Queues.GetAsync("busy") is not null; i++)
        {
            await Task.Delay(100);
        }
        (await host.Queues.GetAsync("busy")).ShouldBeNull("11 idle minutes after the last send");
    }

    [Fact]
    public async Task AtomApi_RejectsAutoDeleteOnIdleBelowFiveMinutes()
    {
        await using var harness = await IntegrationHarness.StartAsync();
        using var http = new HttpClient();

        var body = """
            <entry xmlns="http://www.w3.org/2005/Atom"><content type="application/xml">
            <QueueDescription xmlns="http://schemas.microsoft.com/netservices/2010/10/servicebus/connect">
            <AutoDeleteOnIdle>PT1M</AutoDeleteOnIdle></QueueDescription></content></entry>
            """;
        var response = await http.PutAsync(
            $"http://127.0.0.1:{harness.Port}/too-short?api-version=2021-05",
            new StringContent(body, Encoding.UTF8, "application/atom+xml"));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("at least 5 minutes");
        (await harness.Queues.GetAsync("too-short")).ShouldBeNull();
    }
}
