using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace OpenServiceBus.Samples.FunctionsTriggerDemo.Functions;

/// <summary>
/// HTTP trigger + Service Bus output binding. POST to one of the routes and the body becomes a
/// message on the matching queue. Useful for driving the demo from curl/HTTPie/the browser, and
/// shows that OpenServiceBus also handles the *sending* side of the Functions binding pipeline.
/// </summary>
public sealed class HttpEnqueue
{
    private readonly ILogger<HttpEnqueue> _logger;

    public HttpEnqueue(ILogger<HttpEnqueue> logger) => _logger = logger;

    public sealed class OrderEnqueueResult
    {
        [ServiceBusOutput("orders", Connection = "ServiceBusConnection")]
        public string Message { get; set; } = string.Empty;

        public HttpResponseData? Http { get; set; }
    }

    public sealed class ManualEnqueueResult
    {
        [ServiceBusOutput("manual-queue", Connection = "ServiceBusConnection")]
        public string Message { get; set; } = string.Empty;

        public HttpResponseData? Http { get; set; }
    }

    public sealed class BatchEnqueueResult
    {
        [ServiceBusOutput("batch-queue", Connection = "ServiceBusConnection")]
        public string[] Messages { get; set; } = [];

        public HttpResponseData? Http { get; set; }
    }

    [Function(nameof(EnqueueOrder))]
    public async Task<OrderEnqueueResult> EnqueueOrder(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "orders")] HttpRequestData req)
    {
        var body = await req.ReadAsStringAsync() ?? "default-order";
        _logger.LogInformation("[http] POST /api/orders → enqueue '{Body}'", body);

        var http = req.CreateResponse(HttpStatusCode.Accepted);
        await http.WriteStringAsync($"queued onto 'orders': {body}\n");
        return new OrderEnqueueResult { Message = body, Http = http };
    }

    [Function(nameof(EnqueueManual))]
    public async Task<ManualEnqueueResult> EnqueueManual(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "manual/{verdict}")] HttpRequestData req,
        string verdict)
    {
        _logger.LogInformation("[http] POST /api/manual/{Verdict}", verdict);

        var http = req.CreateResponse(HttpStatusCode.Accepted);
        await http.WriteStringAsync(
            $"queued verdict '{verdict}' - watch for [manual-queue] log line.\n" +
            $"valid verdicts: complete | abandon | deadletter | defer\n");
        return new ManualEnqueueResult { Message = verdict, Http = http };
    }

    [Function(nameof(EnqueueBatch))]
    public async Task<BatchEnqueueResult> EnqueueBatch(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "batch/{count:int}")] HttpRequestData req,
        int count)
    {
        count = Math.Clamp(count, 1, 50);
        var batch = Enumerable.Range(0, count).Select(i => $"batch-msg-{i}").ToArray();
        _logger.LogInformation("[http] POST /api/batch/{Count} → enqueueing {Count} messages", count, count);

        var http = req.CreateResponse(HttpStatusCode.Accepted);
        await http.WriteStringAsync($"queued {count} message(s) onto 'batch-queue'.\n");
        return new BatchEnqueueResult { Messages = batch, Http = http };
    }
}
