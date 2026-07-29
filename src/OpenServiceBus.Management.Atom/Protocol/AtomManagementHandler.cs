using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenServiceBus.Core.Entities;
using OpenServiceBus.Core.Storage;

namespace OpenServiceBus.Management.Atom.Protocol;

/// <summary>
/// The Service Bus ATOM-pub management protocol: entity CRUD for queues, topics,
/// subscriptions, and rules, plus namespace info and runtime counters - the HTTP surface
/// behind <c>ServiceBusAdministrationClient</c> (and the other SDKs' admin clients).
///
/// Route shapes (all under the namespace root, all expecting <c>?api-version=…</c>):
/// <code>
///   GET                 /$namespaceinfo
///   GET                 /$Resources/queues            /$Resources/topics
///   PUT | GET | DELETE  /{entity}
///   GET                 /{topic}/subscriptions
///   PUT | GET | DELETE  /{topic}/subscriptions/{sub}
///   GET                 /{topic}/subscriptions/{sub}/rules
///   PUT | GET | DELETE  /{topic}/subscriptions/{sub}/rules/{rule}
/// </code>
/// PUT without <c>If-Match</c> creates (409 when the entity exists); PUT with <c>If-Match</c>
/// updates in place (404 when it doesn't) - exactly how the SDK distinguishes Create* from
/// Update*. Transport-agnostic: hosting adapters feed it <see cref="AtomHttpRequest"/>s.
/// </summary>
public sealed class AtomManagementHandler
{
    private const int DefaultPageSize = 100;

    private readonly IQueueRegistry _queues;
    private readonly ITopicRegistry _topics;
    private readonly IMessageStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly AtomManagementOptions _options;
    private readonly ILogger _logger;
    private readonly DateTimeOffset _startedAt;

    public AtomManagementHandler(
        IQueueRegistry queues,
        ITopicRegistry topics,
        IMessageStore store,
        TimeProvider timeProvider,
        AtomManagementOptions? options = null,
        ILogger<AtomManagementHandler>? logger = null)
    {
        _queues = queues;
        _topics = topics;
        _store = store;
        _timeProvider = timeProvider;
        _options = options ?? new AtomManagementOptions();
        _logger = logger ?? NullLogger<AtomManagementHandler>.Instance;
        _startedAt = timeProvider.GetUtcNow();
    }

    public async Task<AtomHttpResponse> HandleAsync(AtomHttpRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            if (_options.AuthorizeRequest is { } authorize && !authorize(request.Authorization))
            {
                return Error(401, "Manage claim required. The provided SAS token was rejected.");
            }

            var segments = request.Path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
            {
                return Error(404, "No entity path supplied.");
            }

            if (segments is ["$namespaceinfo"] && request.Method == "GET")
            {
                return NamespaceInfo(request);
            }

            if (string.Equals(segments[0], "$Resources", StringComparison.OrdinalIgnoreCase))
            {
                return segments.Length == 2 && request.Method == "GET"
                    ? segments[1].ToLowerInvariant() switch
                    {
                        "queues" => await ListQueuesAsync(request, cancellationToken).ConfigureAwait(false),
                        "topics" => await ListTopicsAsync(request, cancellationToken).ConfigureAwait(false),
                        _ => Error(404, $"Unknown resource collection '{segments[1]}'."),
                    }
                    : Error(400, "Unsupported $Resources request.");
            }

            var subscriptionsIndex = Array.FindIndex(segments,
                s => string.Equals(s, "subscriptions", StringComparison.OrdinalIgnoreCase));
            if (subscriptionsIndex >= 1)
            {
                var topicName = string.Join('/', segments[..subscriptionsIndex]);
                var rest = segments[(subscriptionsIndex + 1)..];
                // $-segments never name topics or subscriptions; rule names are exempt because
                // the default rule really is called "$Default".
                if (segments[..subscriptionsIndex].Any(s => s.StartsWith('$'))
                    || (rest.Length > 0 && rest[0].StartsWith('$')))
                {
                    return Error(404, $"Entity '{request.Path}' does not exist. SubCode=40400.");
                }
                return rest switch
                {
                    [] when request.Method == "GET" =>
                        await ListSubscriptionsAsync(request, topicName, cancellationToken).ConfigureAwait(false),
                    [var sub] =>
                        await HandleSubscriptionAsync(request, topicName, sub, cancellationToken).ConfigureAwait(false),
                    [var sub, var rules] when string.Equals(rules, "rules", StringComparison.OrdinalIgnoreCase) && request.Method == "GET" =>
                        await ListRulesAsync(request, topicName, sub, cancellationToken).ConfigureAwait(false),
                    [var sub, var rules, var rule] when string.Equals(rules, "rules", StringComparison.OrdinalIgnoreCase) =>
                        await HandleRuleAsync(request, topicName, sub, rule, cancellationToken).ConfigureAwait(false),
                    _ => Error(400, $"Unrecognised management path '{request.Path}'."),
                };
            }

            // Sub-entities of queues (…/$DeadLetterQueue) and other $-segments are not
            // addressable management entities.
            if (segments.Any(s => s.StartsWith('$')))
            {
                return Error(404, $"Entity '{request.Path}' does not exist. SubCode=40400.");
            }

            return await HandleEntityAsync(request, string.Join('/', segments), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is XmlException or FormatException or OverflowException or ArgumentException)
        {
            return Error(400, $"Malformed request: {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ATOM management request {Method} {Path} failed", request.Method, request.Path);
            return Error(500, "Internal error handling the management request.");
        }
    }

    // ── Queue / topic entities ──

    private async Task<AtomHttpResponse> HandleEntityAsync(AtomHttpRequest request, string name, CancellationToken ct)
    {
        switch (request.Method)
        {
            case "GET":
            {
                var queue = await _queues.GetAsync(name, ct).ConfigureAwait(false);
                if (queue is not null && !IsInternalQueue(queue.Name))
                {
                    return EntityOk(200, request, name, queue.CreatedAt, queue.UpdatedAt,
                        EntityDescriptionCodec.QueueDescription(queue, await RuntimeAsync(queue.Name, ct).ConfigureAwait(false)));
                }
                var topic = await _topics.GetTopicAsync(name, ct).ConfigureAwait(false);
                if (topic is not null)
                {
                    var subCount = (await _topics.ListSubscriptionsAsync(topic.Name, ct).ConfigureAwait(false)).Count;
                    return EntityOk(200, request, name, topic.CreatedAt, topic.UpdatedAt,
                        EntityDescriptionCodec.TopicDescription(topic, subCount));
                }
                return Error(404, $"Entity '{name}' does not exist. SubCode=40400.");
            }
            case "PUT":
                return await PutEntityAsync(request, name, ct).ConfigureAwait(false);
            case "DELETE":
            {
                if (await _queues.GetAsync(name, ct).ConfigureAwait(false) is { } queue && !IsInternalQueue(queue.Name))
                {
                    await _queues.DeleteAsync(name, ct).ConfigureAwait(false);
                    return new AtomHttpResponse { StatusCode = 200 };
                }
                if (await _topics.DeleteTopicAsync(name, ct).ConfigureAwait(false))
                {
                    return new AtomHttpResponse { StatusCode = 200 };
                }
                return Error(404, $"Entity '{name}' does not exist. SubCode=40400.");
            }
            default:
                return Error(400, $"Unsupported method {request.Method}.");
        }
    }

    private async Task<AtomHttpResponse> PutEntityAsync(AtomHttpRequest request, string name, CancellationToken ct)
    {
        var description = AtomXml.UnwrapDescription(request.Body);
        if (description is null)
        {
            return Error(400, "Request body must be an ATOM entry containing an entity description.");
        }
        var isUpdate = !string.IsNullOrEmpty(request.IfMatch);
        var now = _timeProvider.GetUtcNow();

        switch (description.Name.LocalName)
        {
            case "QueueDescription":
            {
                var parsed = EntityDescriptionCodec.ParseQueueDescription(name, description);
                var existing = await _queues.GetAsync(name, ct).ConfigureAwait(false);

                if (isUpdate)
                {
                    if (existing is null || IsInternalQueue(name))
                    {
                        return Error(404, $"Queue '{name}' does not exist. SubCode=40400.");
                    }
                    if (parsed.RequiresSession != existing.RequiresSession
                        || parsed.RequiresDuplicateDetection != existing.RequiresDuplicateDetection)
                    {
                        return Error(400, "RequiresSession and RequiresDuplicateDetection cannot be changed after the queue is created. SubCode=40000.");
                    }
                    var updated = await _queues.UpdateAsync(
                        parsed with { CreatedAt = existing.CreatedAt, UpdatedAt = now }, ct).ConfigureAwait(false);
                    return EntityOk(200, request, name, updated.CreatedAt, updated.UpdatedAt,
                        EntityDescriptionCodec.QueueDescription(updated, await RuntimeAsync(name, ct).ConfigureAwait(false)));
                }

                if (existing is not null || await _topics.GetTopicAsync(name, ct).ConfigureAwait(false) is not null)
                {
                    return Error(409, $"An entity named '{name}' already exists. SubCode=40900.");
                }
                var descriptor = parsed with { CreatedAt = now };
                var created = await _queues.CreateAsync(descriptor, ct).ConfigureAwait(false);
                if (!ReferenceEquals(created, descriptor))
                {
                    // Lost a create race - the registry returned a pre-existing descriptor.
                    return Error(409, $"An entity named '{name}' already exists. SubCode=40900.");
                }
                return EntityOk(201, request, name, created.CreatedAt, created.UpdatedAt,
                    EntityDescriptionCodec.QueueDescription(created, await RuntimeAsync(name, ct).ConfigureAwait(false)));
            }
            case "TopicDescription":
            {
                var parsed = EntityDescriptionCodec.ParseTopicDescription(name, description);
                var existing = await _topics.GetTopicAsync(name, ct).ConfigureAwait(false);

                if (isUpdate)
                {
                    if (existing is null)
                    {
                        return Error(404, $"Topic '{name}' does not exist. SubCode=40400.");
                    }
                    var updated = await _topics.UpdateTopicAsync(
                        parsed with { CreatedAt = existing.CreatedAt, UpdatedAt = now }, ct).ConfigureAwait(false);
                    var count = (await _topics.ListSubscriptionsAsync(name, ct).ConfigureAwait(false)).Count;
                    return EntityOk(200, request, name, updated.CreatedAt, updated.UpdatedAt,
                        EntityDescriptionCodec.TopicDescription(updated, count));
                }

                if (existing is not null || await _queues.GetAsync(name, ct).ConfigureAwait(false) is not null)
                {
                    return Error(409, $"An entity named '{name}' already exists. SubCode=40900.");
                }
                var descriptor = parsed with { CreatedAt = now };
                var created = await _topics.CreateTopicAsync(descriptor, ct).ConfigureAwait(false);
                if (!ReferenceEquals(created, descriptor))
                {
                    return Error(409, $"An entity named '{name}' already exists. SubCode=40900.");
                }
                return EntityOk(201, request, name, created.CreatedAt, created.UpdatedAt,
                    EntityDescriptionCodec.TopicDescription(created, 0));
            }
            default:
                return Error(400, $"Unsupported entity description '{description.Name.LocalName}'.");
        }
    }

    // ── Subscriptions ──

    private async Task<AtomHttpResponse> HandleSubscriptionAsync(
        AtomHttpRequest request, string topicName, string subscriptionName, CancellationToken ct)
    {
        switch (request.Method)
        {
            case "GET":
            {
                var sub = await _topics.GetSubscriptionAsync(topicName, subscriptionName, ct).ConfigureAwait(false);
                if (sub is null)
                {
                    return Error(404, $"Subscription '{topicName}/{subscriptionName}' does not exist. SubCode=40400.");
                }
                return EntityOk(200, request, subscriptionName, sub.CreatedAt, sub.UpdatedAt,
                    EntityDescriptionCodec.SubscriptionDescription(sub, await RuntimeAsync(sub.BackingQueueName, ct).ConfigureAwait(false)));
            }
            case "PUT":
                return await PutSubscriptionAsync(request, topicName, subscriptionName, ct).ConfigureAwait(false);
            case "DELETE":
                return await _topics.DeleteSubscriptionAsync(topicName, subscriptionName, ct).ConfigureAwait(false)
                    ? new AtomHttpResponse { StatusCode = 200 }
                    : Error(404, $"Subscription '{topicName}/{subscriptionName}' does not exist. SubCode=40400.");
            default:
                return Error(400, $"Unsupported method {request.Method}.");
        }
    }

    private async Task<AtomHttpResponse> PutSubscriptionAsync(
        AtomHttpRequest request, string topicName, string subscriptionName, CancellationToken ct)
    {
        var description = AtomXml.UnwrapDescription(request.Body);
        if (description is null || description.Name.LocalName != "SubscriptionDescription")
        {
            return Error(400, "Request body must be an ATOM entry containing a SubscriptionDescription.");
        }
        var now = _timeProvider.GetUtcNow();
        var (parsed, defaultRule) = EntityDescriptionCodec.ParseSubscriptionDescription(topicName, subscriptionName, description);
        var existing = await _topics.GetSubscriptionAsync(topicName, subscriptionName, ct).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(request.IfMatch))
        {
            if (existing is null)
            {
                return Error(404, $"Subscription '{topicName}/{subscriptionName}' does not exist. SubCode=40400.");
            }
            if (parsed.RequiresSession != existing.RequiresSession)
            {
                return Error(400, "RequiresSession cannot be changed after the subscription is created. SubCode=40000.");
            }
            var updated = await _topics.UpdateSubscriptionAsync(
                parsed with { CreatedAt = existing.CreatedAt, UpdatedAt = now }, ct).ConfigureAwait(false);
            return EntityOk(200, request, subscriptionName, updated.CreatedAt, updated.UpdatedAt,
                EntityDescriptionCodec.SubscriptionDescription(updated, await RuntimeAsync(updated.BackingQueueName, ct).ConfigureAwait(false)));
        }

        if (existing is not null)
        {
            return Error(409, $"Subscription '{topicName}/{subscriptionName}' already exists. SubCode=40900.");
        }

        var descriptor = parsed with { CreatedAt = now };
        SubscriptionDescriptor created;
        try
        {
            created = await _topics.CreateSubscriptionAsync(descriptor, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("does not exist", StringComparison.Ordinal))
        {
            return Error(404, $"Topic '{topicName}' does not exist. SubCode=40400.");
        }
        if (!ReferenceEquals(created, descriptor))
        {
            return Error(409, $"Subscription '{topicName}/{subscriptionName}' already exists. SubCode=40900.");
        }

        // The SDK can piggyback the initial rule on create; it replaces the implicit
        // $Default TrueFilter rule the registry installs.
        if (defaultRule is not null)
        {
            if (!string.Equals(defaultRule.Name, "$Default", StringComparison.Ordinal))
            {
                await _topics.DeleteRuleAsync(topicName, subscriptionName, "$Default", ct).ConfigureAwait(false);
            }
            await _topics.CreateOrReplaceRuleAsync(defaultRule, ct).ConfigureAwait(false);
        }

        return EntityOk(201, request, subscriptionName, created.CreatedAt, created.UpdatedAt,
            EntityDescriptionCodec.SubscriptionDescription(created, await RuntimeAsync(created.BackingQueueName, ct).ConfigureAwait(false)));
    }

    // ── Rules ──

    private async Task<AtomHttpResponse> HandleRuleAsync(
        AtomHttpRequest request, string topicName, string subscriptionName, string ruleName, CancellationToken ct)
    {
        var rules = await _topics.ListRulesAsync(topicName, subscriptionName, ct).ConfigureAwait(false);
        var existing = rules.FirstOrDefault(r => string.Equals(r.Name, ruleName, StringComparison.OrdinalIgnoreCase));

        switch (request.Method)
        {
            case "GET":
                return existing is null
                    ? Error(404, $"Rule '{ruleName}' does not exist on '{topicName}/{subscriptionName}'. SubCode=40400.")
                    : EntityOk(200, request, existing.Name, _startedAt, null,
                        EntityDescriptionCodec.RuleDescription(existing, _startedAt));
            case "PUT":
            {
                var description = AtomXml.UnwrapDescription(request.Body);
                if (description is null || description.Name.LocalName != "RuleDescription")
                {
                    return Error(400, "Request body must be an ATOM entry containing a RuleDescription.");
                }
                var isUpdate = !string.IsNullOrEmpty(request.IfMatch);
                if (isUpdate && existing is null)
                {
                    return Error(404, $"Rule '{ruleName}' does not exist on '{topicName}/{subscriptionName}'. SubCode=40400.");
                }
                if (!isUpdate && existing is not null)
                {
                    return Error(409, $"Rule '{ruleName}' already exists on '{topicName}/{subscriptionName}'. SubCode=40900.");
                }

                var rule = EntityDescriptionCodec.ParseRuleDescription(topicName, subscriptionName, ruleName, description);
                try
                {
                    await _topics.CreateOrReplaceRuleAsync(rule, ct).ConfigureAwait(false);
                }
                catch (InvalidOperationException)
                {
                    return Error(404, $"Subscription '{topicName}/{subscriptionName}' does not exist. SubCode=40400.");
                }
                var now = _timeProvider.GetUtcNow();
                return EntityOk(isUpdate ? 200 : 201, request, ruleName, now, null,
                    EntityDescriptionCodec.RuleDescription(rule, now));
            }
            case "DELETE":
                return await _topics.DeleteRuleAsync(topicName, subscriptionName, ruleName, ct).ConfigureAwait(false)
                    ? new AtomHttpResponse { StatusCode = 200 }
                    : Error(404, $"Rule '{ruleName}' does not exist on '{topicName}/{subscriptionName}'. SubCode=40400.");
            default:
                return Error(400, $"Unsupported method {request.Method}.");
        }
    }

    // ── Collections ──

    private async Task<AtomHttpResponse> ListQueuesAsync(AtomHttpRequest request, CancellationToken ct)
    {
        var all = (await _queues.ListAsync(ct).ConfigureAwait(false))
            .Where(q => !IsInternalQueue(q.Name))
            .OrderBy(q => q.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var entries = new List<XElement>();
        foreach (var queue in Page(all, request))
        {
            entries.Add(AtomXml.Entry(BaseUrl(request), queue.Name, queue.Name, queue.CreatedAt,
                queue.UpdatedAt ?? queue.CreatedAt,
                EntityDescriptionCodec.QueueDescription(queue, await RuntimeAsync(queue.Name, ct).ConfigureAwait(false))));
        }
        return FeedOk(request, "queues", entries);
    }

    private async Task<AtomHttpResponse> ListTopicsAsync(AtomHttpRequest request, CancellationToken ct)
    {
        var all = (await _topics.ListTopicsAsync(ct).ConfigureAwait(false))
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var entries = new List<XElement>();
        foreach (var topic in Page(all, request))
        {
            var subCount = (await _topics.ListSubscriptionsAsync(topic.Name, ct).ConfigureAwait(false)).Count;
            entries.Add(AtomXml.Entry(BaseUrl(request), topic.Name, topic.Name, topic.CreatedAt,
                topic.UpdatedAt ?? topic.CreatedAt,
                EntityDescriptionCodec.TopicDescription(topic, subCount)));
        }
        return FeedOk(request, "topics", entries);
    }

    private async Task<AtomHttpResponse> ListSubscriptionsAsync(AtomHttpRequest request, string topicName, CancellationToken ct)
    {
        if (await _topics.GetTopicAsync(topicName, ct).ConfigureAwait(false) is null)
        {
            return Error(404, $"Topic '{topicName}' does not exist. SubCode=40400.");
        }
        var all = (await _topics.ListSubscriptionsAsync(topicName, ct).ConfigureAwait(false))
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var entries = new List<XElement>();
        foreach (var sub in Page(all, request))
        {
            entries.Add(AtomXml.Entry(BaseUrl(request), $"{topicName}/subscriptions/{sub.Name}", sub.Name,
                sub.CreatedAt, sub.UpdatedAt ?? sub.CreatedAt,
                EntityDescriptionCodec.SubscriptionDescription(sub, await RuntimeAsync(sub.BackingQueueName, ct).ConfigureAwait(false))));
        }
        return FeedOk(request, "subscriptions", entries);
    }

    private async Task<AtomHttpResponse> ListRulesAsync(AtomHttpRequest request, string topicName, string subscriptionName, CancellationToken ct)
    {
        if (await _topics.GetSubscriptionAsync(topicName, subscriptionName, ct).ConfigureAwait(false) is null)
        {
            return Error(404, $"Subscription '{topicName}/{subscriptionName}' does not exist. SubCode=40400.");
        }
        var all = (await _topics.ListRulesAsync(topicName, subscriptionName, ct).ConfigureAwait(false))
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var entries = Page(all, request)
            .Select(rule => AtomXml.Entry(BaseUrl(request),
                $"{topicName}/subscriptions/{subscriptionName}/rules/{rule.Name}", rule.Name,
                _startedAt, _startedAt,
                EntityDescriptionCodec.RuleDescription(rule, _startedAt)))
            .ToList();
        return FeedOk(request, "rules", entries);
    }

    // ── Namespace ──

    private AtomHttpResponse NamespaceInfo(AtomHttpRequest request)
    {
        var name = request.HostHeader.Split(':')[0];
        var entry = AtomXml.Entry(BaseUrl(request), "$namespaceinfo", name, _startedAt, _startedAt,
            EntityDescriptionCodec.NamespaceInfo(name, _startedAt));
        return new AtomHttpResponse
        {
            StatusCode = 200,
            ContentType = AtomXml.EntryContentType,
            Body = AtomXml.Serialize(entry),
        };
    }

    // ── Helpers ──

    /// <summary>
    /// Message counters for a queue-like entity, including its DLQ sibling - the numbers
    /// behind MessageCount/CountDetails/SizeInBytes.
    /// </summary>
    private async Task<EntityRuntimeInfo> RuntimeAsync(string queueName, CancellationToken ct)
    {
        var messages = _store.Peek(queueName, 0, int.MaxValue);
        var scheduled = messages.Count(m => m.ScheduledEnqueueTime is not null);
        long size = messages.Sum(m => (long)m.EncodedMessage.Length);
        var total = await _store.CountAsync(queueName, ct).ConfigureAwait(false);

        long deadLettered = 0;
        var dlqName = queueName + EntityNames.DeadLetterSuffix;
        if (await _queues.GetAsync(dlqName, ct).ConfigureAwait(false) is not null)
        {
            deadLettered = await _store.CountAsync(dlqName, ct).ConfigureAwait(false);
            size += _store.Peek(dlqName, 0, int.MaxValue).Sum(m => (long)m.EncodedMessage.Length);
        }
        return new EntityRuntimeInfo(
            ActiveMessageCount: Math.Max(0, total - scheduled),
            ScheduledMessageCount: scheduled,
            DeadLetterMessageCount: deadLettered,
            SizeInBytes: size);
    }

    /// <summary>DLQ sub-entities and subscription backing queues are not addressable queues.</summary>
    private static bool IsInternalQueue(string name) =>
        EntityNames.IsDeadLetterQueue(name)
        || name.Contains(EntityNames.SubscriptionsSegment, StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<T> Page<T>(IReadOnlyList<T> items, AtomHttpRequest request)
    {
        var skip = request.Query.TryGetValue("$skip", out var s) && int.TryParse(s, out var sv) && sv > 0 ? sv : 0;
        var top = request.Query.TryGetValue("$top", out var t) && int.TryParse(t, out var tv) && tv > 0 ? tv : DefaultPageSize;
        return items.Skip(skip).Take(top);
    }

    private static string BaseUrl(AtomHttpRequest request) => $"http://{request.HostHeader}";

    private AtomHttpResponse EntityOk(
        int statusCode, AtomHttpRequest request, string title,
        DateTimeOffset created, DateTimeOffset? updated, XElement description)
    {
        var entry = AtomXml.Entry(BaseUrl(request), request.Path.Trim('/'), title, created, updated ?? created, description);
        return new AtomHttpResponse
        {
            StatusCode = statusCode,
            ContentType = AtomXml.EntryContentType,
            Body = AtomXml.Serialize(entry),
        };
    }

    private AtomHttpResponse FeedOk(AtomHttpRequest request, string collectionTitle, IEnumerable<XElement> entries)
    {
        var feed = AtomXml.Feed(BaseUrl(request), collectionTitle, _timeProvider.GetUtcNow(), entries);
        return new AtomHttpResponse
        {
            StatusCode = 200,
            ContentType = AtomXml.FeedContentType,
            Body = AtomXml.Serialize(feed),
        };
    }

    private static AtomHttpResponse Error(int statusCode, string detail) => new()
    {
        StatusCode = statusCode,
        Body = AtomXml.ErrorBody(statusCode, detail),
    };
}
