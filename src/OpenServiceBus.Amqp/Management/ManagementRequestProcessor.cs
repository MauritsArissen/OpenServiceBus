using Amqp;
using Amqp.Framing;
using Amqp.Listener;
using Amqp.Types;
using Microsoft.Extensions.Logging;
using OpenServiceBus.Amqp.Topics;
using OpenServiceBus.Core.Entities;
using OpenServiceBus.Core.Filters;
using OpenServiceBus.Core.Messaging;
using OpenServiceBus.Core.Routing;
using OpenServiceBus.Core.Storage;

namespace OpenServiceBus.Amqp.Management;

/// <summary>
/// Handles AMQP <c>$management</c> request/response operations for a single queue entity.
/// Registered per entity by <see cref="AmqpListenerHost"/> on <c>QueueCreated</c>.
///
/// Operations implemented:
/// <list type="bullet">
///   <item><c>com.microsoft:renew-lock</c> - extends one or more peek-lock deadlines.</item>
///   <item><c>com.microsoft:schedule-message</c> - enqueue messages for future delivery.</item>
///   <item><c>com.microsoft:cancel-scheduled-message</c> - cancel scheduled messages by sequence number.</item>
/// </list>
///
/// Wire contract verified against <c>Azure.Messaging.ServiceBus.Amqp.ManagementConstants</c>:
/// response <c>application-properties</c> keys are <b>camelCase</b> (<c>statusCode</c>, <c>statusDescription</c>) -
/// this differs from <c>$cbs</c> which is kebab-case.
/// </summary>
public sealed class ManagementRequestProcessor : IRequestNodeHandler
{
    private const string OperationKey = "operation";
    private const string AssociatedLinkNameKey = "associated-link-name";
    private const string StatusCodeKey = "statusCode";
    private const string StatusDescriptionKey = "statusDescription";

    private const string RenewLockOperation = "com.microsoft:renew-lock";
    private const string ScheduleMessageOperation = "com.microsoft:schedule-message";
    private const string CancelScheduledMessageOperation = "com.microsoft:cancel-scheduled-message";
    private const string PeekMessageOperation = "com.microsoft:peek-message";
    private const string ReceiveBySequenceNumberOperation = "com.microsoft:receive-by-sequence-number";
    private const string UpdateDispositionOperation = "com.microsoft:update-disposition";

    // Rule management ops, scoped to a subscription's $management endpoint.
    private const string AddRuleOperation = "com.microsoft:add-rule";
    private const string RemoveRuleOperation = "com.microsoft:remove-rule";
    private const string EnumerateRulesOperation = "com.microsoft:enumerate-rules";
    private const string RuleNameBodyKey = "rule-name";
    private const string RuleDescriptionBodyKey = "rule-description";
    private const string RulesResponseKey = "rules";
    private const string EnumerateTopKey = "top";
    private const string EnumerateSkipKey = "skip";

    // Session ops, available on every queue/subscription $management endpoint.
    private const string SetSessionStateOperation = "com.microsoft:set-session-state";
    private const string GetSessionStateOperation = "com.microsoft:get-session-state";
    private const string RenewSessionLockOperation = "com.microsoft:renew-session-lock";
    private const string GetMessageSessionsOperation = "com.microsoft:get-message-sessions";
    private const string SessionIdBodyKey = "session-id";
    private const string SessionStateBodyKey = "session-state";
    private const string ExpirationBodyKey = "expiration";
    private const string SessionsIdsBodyKey = "sessions-ids";

    private const string LockTokensBodyKey = "lock-tokens";
    private const string LockTokenBodyKey = "lock-token";
    private const string ExpirationsBodyKey = "expirations";
    private const string MessagesBodyKey = "messages";
    private const string MessageBodyKey = "message";
    private const string SequenceNumbersBodyKey = "sequence-numbers";
    private const string FromSequenceNumberBodyKey = "from-sequence-number";
    private const string MessageCountBodyKey = "message-count";
    private const string DispositionStatusBodyKey = "disposition-status";
    private const string DeadLetterReasonBodyKey = "deadletter-reason";
    private const string DeadLetterDescriptionBodyKey = "deadletter-description";

    // Wire values for disposition-status (lowercased Enum.ToString() from the SDK's DispositionStatus).
    private const string DispositionCompleted = "completed";
    private const string DispositionAbandoned = "abandoned";
    private const string DispositionDeferred = "defered";          // SDK enum is mis-spelled - match it on the wire
    private const string DispositionSuspended = "suspended";       // = dead-letter

    private static readonly Symbol ScheduledEnqueueTimeSymbol = new("x-opt-scheduled-enqueue-time");
    private static readonly Symbol MessageStateSymbol = new("x-opt-message-state");
    private static readonly Symbol EnqueuedTimeUtcSymbol = new("x-opt-enqueued-time");
    private static readonly Symbol SequenceNumberSymbol = new("x-opt-sequence-number");

    // Service Bus message states the SDK reports via ServiceBusReceivedMessage.State.
    private const int MessageStateActive = 0;
    private const int MessageStateDeferred = 1;
    private const int MessageStateScheduled = 2;

    private readonly string _entityName;
    private readonly QueueDescriptor _descriptor;
    private readonly IMessageStore _store;
    private readonly IMessageRouter _router;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ManagementRequestProcessor> _logger;

    // Set only when this processor sits in front of a subscription. Enables add-rule /
    // remove-rule / enumerate-rules ops; null for queue endpoints.
    private readonly ITopicRegistry? _topics;
    private readonly string? _topicName;
    private readonly string? _subscriptionName;

    public ManagementRequestProcessor(
        string entityName,
        QueueDescriptor descriptor,
        IMessageStore store,
        IMessageRouter router,
        TimeProvider timeProvider,
        ILogger<ManagementRequestProcessor> logger)
    {
        _entityName = entityName;
        _descriptor = descriptor;
        _store = store;
        _router = router;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Overload that adds subscription context - enables the rule-management operations.
    /// <paramref name="entityName"/> is still the storage entity (the subscription's backing
    /// queue, e.g. <c>events/Subscriptions/eu</c>) so peek-lock and disposition ops continue to
    /// land on the right messages.
    /// </summary>
    public ManagementRequestProcessor(
        string entityName,
        QueueDescriptor descriptor,
        IMessageStore store,
        IMessageRouter router,
        TimeProvider timeProvider,
        ILogger<ManagementRequestProcessor> logger,
        ITopicRegistry topics,
        string topicName,
        string subscriptionName)
        : this(entityName, descriptor, store, router, timeProvider, logger)
    {
        _topics = topics;
        _topicName = topicName;
        _subscriptionName = subscriptionName;
    }

    public int Credit => 100;

    public Message HandleRequest(Message request, Connection? connection)
    {
        _ = connection;
        var operation = GetOperation(request);

        try
        {
            return operation switch
            {
                RenewLockOperation => HandleRenewLock(request),
                ScheduleMessageOperation => HandleScheduleMessage(request),
                CancelScheduledMessageOperation => HandleCancelScheduledMessage(request),
                PeekMessageOperation => HandlePeekMessage(request),
                ReceiveBySequenceNumberOperation => HandleReceiveBySequenceNumber(request),
                UpdateDispositionOperation => HandleUpdateDisposition(request),
                AddRuleOperation when _topics is not null => HandleAddRule(request),
                RemoveRuleOperation when _topics is not null => HandleRemoveRule(request),
                EnumerateRulesOperation when _topics is not null => HandleEnumerateRules(request),
                AddRuleOperation or RemoveRuleOperation or EnumerateRulesOperation =>
                    BuildResponse(request, 404, "NotFound: rule operations are only available on subscription $management endpoints."),
                SetSessionStateOperation => HandleSetSessionState(request),
                GetSessionStateOperation => HandleGetSessionState(request),
                RenewSessionLockOperation => HandleRenewSessionLock(request),
                GetMessageSessionsOperation => HandleGetMessageSessions(request),
                _ => BuildResponse(request, statusCode: 501, statusDescription: $"NotImplemented: {operation}"),
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "$management {Op} failed on {Entity}", operation, _entityName);
            return BuildResponse(request, statusCode: 500, statusDescription: "InternalError: " + ex.Message);
        }
    }

    private Message HandleRenewLock(Message request)
    {
        if (request.Body is not Map body || !body.TryGetValue(LockTokensBodyKey, out var lockTokensObj))
        {
            return BuildResponse(request, 400, "BadRequest: missing 'lock-tokens' in body");
        }

        var lockTokens = ExtractGuidArray(lockTokensObj);
        if (lockTokens.Length == 0)
        {
            return BuildResponse(request, 400, "BadRequest: empty 'lock-tokens'");
        }

        // The SDK includes `associated-link-name` so the broker can verify the renew is being
        // issued by the same receiver that took the lock.
        var requestingLink = request.ApplicationProperties?[AssociatedLinkNameKey] as string;

        var expirations = new DateTime[lockTokens.Length];
        for (var i = 0; i < lockTokens.Length; i++)
        {
            var newUntil = _store.TryRenewLockAsync(_entityName, lockTokens[i], _descriptor.LockDuration, requestingLink)
                .GetAwaiter().GetResult();
            if (newUntil is null)
            {
                // Service Bus surfaces "lock lost" as 410 Gone - for unknown lock OR cross-link renew.
                return BuildResponse(request, 410, $"Gone: lock token {lockTokens[i]} is unknown, expired, or held by a different link");
            }
            expirations[i] = newUntil.Value.UtcDateTime;
        }

        var responseBody = new Map { [ExpirationsBodyKey] = expirations };
        var response = BuildResponse(request, 200, "OK");
        response.BodySection = new AmqpValue { Value = responseBody };
        return response;
    }

    /// <summary>
    /// Schedule one or more messages. The body's <c>messages</c> array contains entries where
    /// <c>message</c> is the encoded AMQP payload; the scheduled delivery time lives inside that
    /// payload as the <c>x-opt-scheduled-enqueue-time</c> annotation. Returns the assigned
    /// sequence numbers so the SDK can later cancel them.
    /// </summary>
    private Message HandleScheduleMessage(Message request)
    {
        // Scheduling is a send: a (Send)Disabled entity rejects it like a transfer would.
        if (!_descriptor.Status.AcceptsSends())
        {
            return BuildResponse(request, 403, $"Forbidden: entity '{_entityName}' is {_descriptor.Status} and does not accept messages.");
        }

        if (request.Body is not Map body || !body.TryGetValue(MessagesBodyKey, out var messagesObj))
        {
            return BuildResponse(request, 400, "BadRequest: missing 'messages' in body");
        }

        // Microsoft.Azure.Amqp sends List<AmqpMap> which arrives as Amqp.Types.List (= List<object>) of Map.
        var entries = messagesObj switch
        {
            Map[] m => m,
            object[] a => a.OfType<Map>().ToArray(),
            System.Collections.IList list => list.OfType<Map>().ToArray(),
            _ => Array.Empty<Map>(),
        };
        if (entries.Length == 0)
        {
            return BuildResponse(request, 400, "BadRequest: empty 'messages' list");
        }

        var sequenceNumbers = new long[entries.Length];
        for (var i = 0; i < entries.Length; i++)
        {
            if (!entries[i].TryGetValue(MessageBodyKey, out var bytesObj))
            {
                return BuildResponse(request, 400, $"BadRequest: missing 'message' in entry {i}");
            }
            var encoded = ExtractMessageBytes(bytesObj);
            if (encoded is null)
            {
                return BuildResponse(request, 400, $"BadRequest: 'message' in entry {i} must be binary");
            }

            var amqp = DecodeMessage(encoded);
            var scheduledTime = ReadScheduledEnqueueTime(amqp) ?? _timeProvider.GetUtcNow();
            var expiresAt = ComputeExpiresAt(amqp);
            // Mirror the send path: only honor the session id on session-enabled entities
            // (a GroupId on a plain queue would strand the activated message in a session
            // channel no receiver reads), and reject session-less schedules on session
            // queues the same way Azure does.
            var sessionId = _descriptor.RequiresSession ? amqp.Properties?.GroupId : null;
            if (_descriptor.RequiresSession && string.IsNullOrEmpty(sessionId))
            {
                return BuildResponse(request, 400,
                    $"BadRequest: A SessionId is required for messages scheduled on session-enabled entity '{_entityName}'.");
            }

            // Honor duplicate detection on the schedule path too - otherwise scheduling the
            // same MessageId twice delivers two copies, unlike the normal send path.
            // (Auto-forwarding is deliberately not applied here: the caller needs a stable
            // sequence number on THIS entity to cancel the scheduled message later, which a
            // forwarded copy on another entity could not provide.)
            var messageId = _descriptor.RequiresDuplicateDetection ? amqp.Properties?.MessageId?.ToString() : null;
            var dedupWindow = _descriptor.RequiresDuplicateDetection
                ? _descriptor.DuplicateDetectionHistoryTimeWindow ?? TimeSpan.FromMinutes(10)
                : (TimeSpan?)null;

            var stored = _store.EnqueueAsync(_entityName, encoded, expiresAt, scheduledTime, sessionId, messageId, dedupWindow).GetAwaiter().GetResult();
            sequenceNumbers[i] = stored.SequenceNumber;
        }

        var responseBody = new Map { [SequenceNumbersBodyKey] = sequenceNumbers };
        var response = BuildResponse(request, 200, "OK");
        response.BodySection = new AmqpValue { Value = responseBody };
        return response;
    }

    /// <summary>
    /// Read messages without locking. Request body has <c>from-sequence-number</c> (long) and
    /// <c>message-count</c> (int). Response body has a <c>messages</c> list, each entry being
    /// a map with <c>message</c> → encoded bytes. Each outgoing message has fresh stamps for
    /// <c>x-opt-sequence-number</c>, <c>x-opt-enqueued-time</c>, and <c>x-opt-message-state</c>
    /// so the SDK can surface <see cref="ServiceBusMessageState"/> correctly.
    /// </summary>
    private Message HandlePeekMessage(Message request)
    {
        if (request.Body is not Map body)
        {
            return BuildResponse(request, 400, "BadRequest: missing peek body");
        }
        var fromSeq = body.TryGetValue(FromSequenceNumberBodyKey, out var fObj) ? Convert.ToInt64(fObj) : 0L;
        var maxCount = body.TryGetValue(MessageCountBodyKey, out var cObj) ? Convert.ToInt32(cObj) : 1;
        if (maxCount <= 0) maxCount = 1;

        var peeked = _store.Peek(_entityName, fromSeq, maxCount);
        if (peeked.Count == 0)
        {
            // Service Bus returns 204 No Content for an empty peek window.
            return BuildResponse(request, 204, "NoContent");
        }

        var entries = new List<Map>(peeked.Count);
        foreach (var stored in peeked)
        {
            var amqp = DecodeMessage(stored.EncodedMessage);
            // Stamp the same broker-authoritative fields a normal delivery would carry.
            // The header matters too: ServiceBusReceivedMessage.DeliveryCount does
            // Header.DeliveryCount.Value and throws on a peeked message without it.
            amqp.Header ??= new Header();
            amqp.Header.DeliveryCount = (uint)stored.DeliveryCount;
            amqp.MessageAnnotations ??= new MessageAnnotations();
            amqp.MessageAnnotations.Map[SequenceNumberSymbol] = stored.SequenceNumber;
            amqp.MessageAnnotations.Map[EnqueuedTimeUtcSymbol] = stored.EnqueuedAt.UtcDateTime;
            amqp.MessageAnnotations.Map[MessageStateSymbol] = stored.ScheduledEnqueueTime is not null
                ? MessageStateScheduled
                : stored.IsDeferred
                    ? MessageStateDeferred
                    : MessageStateActive;

            var rebytes = ReEncode(amqp);
            entries.Add(new Map { [MessageBodyKey] = rebytes });
        }

        var responseBody = new Map { [MessagesBodyKey] = entries };
        var response = BuildResponse(request, 200, "OK");
        response.BodySection = new AmqpValue { Value = responseBody };
        return response;
    }

    private static byte[] ReEncode(Message msg)
    {
        var buf = msg.Encode();
        var copy = new byte[buf.Length];
        Array.Copy(buf.Buffer, buf.Offset, copy, 0, buf.Length);
        return copy;
    }

    /// <summary>
    /// Retrieve deferred messages by sequence number. Each retrieved message gets a fresh
    /// peek-lock; abandon via update-disposition returns it to Deferred state.
    /// Request body: <c>sequence-numbers</c> (long[]), optional <c>receiver-settle-mode</c>.
    /// Response body: <c>messages</c> array, each entry with <c>message</c> bytes + <c>lock-token</c> Guid.
    /// </summary>
    private Message HandleReceiveBySequenceNumber(Message request)
    {
        if (request.Body is not Map body || !body.TryGetValue(SequenceNumbersBodyKey, out var seqsObj))
        {
            return BuildResponse(request, 400, "BadRequest: missing 'sequence-numbers'");
        }
        var sequenceNumbers = ExtractLongArray(seqsObj);
        if (sequenceNumbers.Length == 0)
        {
            return BuildResponse(request, 400, "BadRequest: empty 'sequence-numbers'");
        }
        var requestingLink = request.ApplicationProperties?[AssociatedLinkNameKey] as string;

        var entries = new List<Map>(sequenceNumbers.Length);
        foreach (var seq in sequenceNumbers)
        {
            var locked = _store.TryReceiveDeferredAsync(_entityName, seq, _descriptor.LockDuration, requestingLink)
                .GetAwaiter().GetResult();
            if (locked is null) continue; // Silently skip non-deferred / unknown seq numbers.

            var amqp = DecodeMessage(locked.Message.EncodedMessage);
            // Stamp the same broker-authoritative annotations a normal delivery carries.
            amqp.Header ??= new Header();
            amqp.Header.DeliveryCount = (uint)locked.Message.DeliveryCount;
            amqp.MessageAnnotations ??= new MessageAnnotations();
            amqp.MessageAnnotations.Map[SequenceNumberSymbol] = locked.Message.SequenceNumber;
            amqp.MessageAnnotations.Map[EnqueuedTimeUtcSymbol] = locked.Message.EnqueuedAt.UtcDateTime;
            amqp.MessageAnnotations.Map[new Symbol("x-opt-locked-until")] = locked.LockedUntil.UtcDateTime;

            entries.Add(new Map
            {
                [MessageBodyKey] = ReEncode(amqp),
                [LockTokenBodyKey] = locked.LockToken,
            });
        }

        if (entries.Count == 0)
        {
            return BuildResponse(request, 204, "NoContent");
        }

        var responseBody = new Map { [MessagesBodyKey] = entries };
        var response = BuildResponse(request, 200, "OK");
        response.BodySection = new AmqpValue { Value = responseBody };
        return response;
    }

    /// <summary>
    /// Settle a message previously retrieved via receive-by-sequence-number.
    /// Request body: <c>lock-tokens</c> (Guid[]), <c>disposition-status</c> string,
    /// optional <c>deadletter-reason</c> / <c>deadletter-description</c>.
    /// Status values (lower-case <c>Enum.ToString()</c> from the SDK):
    /// "completed", "abandoned", "defered" (SDK typo, preserved on the wire), "suspended" (= dead-letter).
    /// </summary>
    private Message HandleUpdateDisposition(Message request)
    {
        if (request.Body is not Map body || !body.TryGetValue(LockTokensBodyKey, out var lockTokensObj))
        {
            return BuildResponse(request, 400, "BadRequest: missing 'lock-tokens'");
        }
        var lockTokens = ExtractGuidArray(lockTokensObj);
        if (lockTokens.Length == 0)
        {
            return BuildResponse(request, 400, "BadRequest: empty 'lock-tokens'");
        }
        var status = (body.TryGetValue(DispositionStatusBodyKey, out var s) ? s as string : null)?.ToLowerInvariant();
        if (status is null)
        {
            return BuildResponse(request, 400, "BadRequest: missing 'disposition-status'");
        }

        var reason = body.TryGetValue(DeadLetterReasonBodyKey, out var r) ? r as string : null;
        var description = body.TryGetValue(DeadLetterDescriptionBodyKey, out var d) ? d as string : null;

        foreach (var token in lockTokens)
        {
            switch (status)
            {
                case DispositionCompleted:
                    _store.TryCompleteAsync(_entityName, token).GetAwaiter().GetResult();
                    break;
                case DispositionAbandoned:
                    _store.TryAbandonAsync(_entityName, token).GetAwaiter().GetResult();
                    break;
                case DispositionDeferred:
                    _store.TryDeferAsync(_entityName, token).GetAwaiter().GetResult();
                    break;
                case DispositionSuspended:
                    DeadLetterViaManagementAsync(token, reason, description).GetAwaiter().GetResult();
                    break;
                default:
                    return BuildResponse(request, 400, $"BadRequest: unknown disposition-status '{status}'");
            }
        }

        return BuildResponse(request, 200, "OK");
    }

    /// <summary>
    /// Move a locked message to the DLQ via the management path (when the original receiver link
    /// is no longer in scope - e.g. for a message retrieved via receive-by-sequence-number).
    /// Mirrors <c>QueueReceiverSource.DeadLetterAsync</c> at this layer.
    /// </summary>
    private async Task DeadLetterViaManagementAsync(Guid lockToken, string? reason, string? description)
    {
        if (EntityNames.IsDeadLetterQueue(_entityName))
        {
            await _store.TryAbandonAsync(_entityName, lockToken).ConfigureAwait(false);
            return;
        }

        var removed = await _store.TryRemoveLockedAsync(_entityName, lockToken).ConfigureAwait(false);
        if (removed is null) return;

        var dlqBytes = DeadLettering.DeadLetterEncoder.AppendDeadLetterHeaders(removed.EncodedMessage, _entityName, reason, description);
        // Honor ForwardDeadLetteredMessagesTo when set, fallback to local DLQ.
        var dlqTarget = string.IsNullOrEmpty(_descriptor.ForwardDeadLetteredMessagesTo)
            ? _entityName + EntityNames.DeadLetterSuffix
            : _descriptor.ForwardDeadLetteredMessagesTo!;
        // Management-plane dead-letter always settles a delivery the client is holding
        // (receive-by-sequence / deferred), so that delivery counts toward the history.
        await _router.RouteAsync(dlqTarget, dlqBytes, deliveryCount: removed.DeliveryCount + 1).ConfigureAwait(false);
    }

    private Message HandleAddRule(Message request)
    {
        if (request.Body is not Map body || !body.TryGetValue(RuleNameBodyKey, out var nameObj) || nameObj is not string ruleName || string.IsNullOrWhiteSpace(ruleName))
        {
            return BuildResponse(request, 400, "BadRequest: missing or empty 'rule-name'");
        }
        if (!body.TryGetValue(RuleDescriptionBodyKey, out var descObj))
        {
            return BuildResponse(request, 400, "BadRequest: missing 'rule-description'");
        }

        RuleFilter filter;
        SqlRuleAction? action;
        try
        {
            (filter, action) = RuleWireCodec.DecodeRule(descObj!);
        }
        catch (ArgumentException ex)
        {
            return BuildResponse(request, 400, "BadRequest: " + ex.Message);
        }
        catch (FormatException ex)
        {
            // SqlFilter / SqlRuleAction parse errors surface here.
            return BuildResponse(request, 400, "BadRequest: " + ex.Message);
        }

        // Service Bus's add-rule is upsert-style - if the rule already exists with the same
        // name and same definition it's a no-op; if the name exists with a different
        // definition that's a conflict. We treat any same-name call as a replace, which
        // matches how the in-memory TopicManager already behaves and avoids ambiguity.
        try
        {
            _topics!.CreateOrReplaceRuleAsync(new RuleDescriptor
            {
                TopicName = _topicName!,
                SubscriptionName = _subscriptionName!,
                Name = ruleName,
                Filter = filter,
                Action = action,
            }).GetAwaiter().GetResult();
        }
        catch (InvalidOperationException ex)
        {
            return BuildResponse(request, 404, "NotFound: " + ex.Message);
        }

        return BuildResponse(request, 200, "OK");
    }

    private Message HandleRemoveRule(Message request)
    {
        if (request.Body is not Map body || !body.TryGetValue(RuleNameBodyKey, out var nameObj) || nameObj is not string ruleName || string.IsNullOrWhiteSpace(ruleName))
        {
            return BuildResponse(request, 400, "BadRequest: missing or empty 'rule-name'");
        }
        var deleted = _topics!.DeleteRuleAsync(_topicName!, _subscriptionName!, ruleName).GetAwaiter().GetResult();
        return deleted
            ? BuildResponse(request, 200, "OK")
            : BuildResponse(request, 404, $"NotFound: no rule '{ruleName}' on {_topicName}/{_subscriptionName}");
    }

    private Message HandleEnumerateRules(Message request)
    {
        var top = 100;
        var skip = 0;
        if (request.Body is Map body)
        {
            if (body.TryGetValue(EnumerateTopKey, out var t)) top = Convert.ToInt32(t);
            if (body.TryGetValue(EnumerateSkipKey, out var s)) skip = Convert.ToInt32(s);
        }
        if (top <= 0) top = 100;
        if (skip < 0) skip = 0;

        var rules = _topics!.ListRulesAsync(_topicName!, _subscriptionName!).GetAwaiter().GetResult();
        var page = rules.Skip(skip).Take(top).ToArray();

        var entries = new List<Map>(page.Length);
        foreach (var rule in page)
        {
            entries.Add(new Map
            {
                [RuleNameBodyKey] = rule.Name,
                [RuleDescriptionBodyKey] = RuleWireCodec.EncodeRuleDescription(rule.Name, rule.Filter, rule.Action),
            });
        }

        if (entries.Count == 0)
        {
            return BuildResponse(request, 204, "NoContent");
        }

        var responseBody = new Map { [RulesResponseKey] = entries };
        var response = BuildResponse(request, 200, "OK");
        response.BodySection = new AmqpValue { Value = responseBody };
        return response;
    }

    private Message HandleSetSessionState(Message request)
    {
        if (request.Body is not Map body || !body.TryGetValue(SessionIdBodyKey, out var idObj) || idObj is not string sessionId || string.IsNullOrEmpty(sessionId))
        {
            return BuildResponse(request, 400, "BadRequest: missing or empty 'session-id'");
        }
        var stateObj = body.TryGetValue(SessionStateBodyKey, out var s) ? s : null;
        var stateBytes = stateObj switch
        {
            byte[] bytes => bytes,
            ArraySegment<byte> seg => seg.ToArray(),
            null => null,
            _ => null,
        };
        _store.SetSessionStateAsync(_entityName, sessionId, stateBytes).GetAwaiter().GetResult();
        return BuildResponse(request, 200, "OK");
    }

    private Message HandleGetSessionState(Message request)
    {
        if (request.Body is not Map body || !body.TryGetValue(SessionIdBodyKey, out var idObj) || idObj is not string sessionId || string.IsNullOrEmpty(sessionId))
        {
            return BuildResponse(request, 400, "BadRequest: missing or empty 'session-id'");
        }
        var state = _store.GetSessionStateAsync(_entityName, sessionId).GetAwaiter().GetResult();
        var responseBody = new Map { [SessionStateBodyKey] = state };
        var response = BuildResponse(request, 200, "OK");
        response.BodySection = new AmqpValue { Value = responseBody };
        return response;
    }

    private Message HandleRenewSessionLock(Message request)
    {
        if (request.Body is not Map body || !body.TryGetValue(SessionIdBodyKey, out var idObj) || idObj is not string sessionId || string.IsNullOrEmpty(sessionId))
        {
            return BuildResponse(request, 400, "BadRequest: missing or empty 'session-id'");
        }
        var requestingLink = request.ApplicationProperties?[AssociatedLinkNameKey] as string;
        var newUntil = _store.TryRenewSessionLockAsync(_entityName, sessionId, _descriptor.LockDuration, requestingLink).GetAwaiter().GetResult();
        if (newUntil is null)
        {
            return BuildResponse(request, 410, $"Gone: session lock for '{sessionId}' is unknown, expired, or held by a different link");
        }
        var responseBody = new Map { [ExpirationBodyKey] = newUntil.Value.UtcDateTime };
        var response = BuildResponse(request, 200, "OK");
        response.BodySection = new AmqpValue { Value = responseBody };
        return response;
    }

    private Message HandleGetMessageSessions(Message request)
    {
        var sessions = _store.ListSessions(_entityName);
        if (sessions.Count == 0)
        {
            return BuildResponse(request, 204, "NoContent");
        }
        var responseBody = new Map { [SessionsIdsBodyKey] = sessions.ToArray() };
        var response = BuildResponse(request, 200, "OK");
        response.BodySection = new AmqpValue { Value = responseBody };
        return response;
    }

    private Message HandleCancelScheduledMessage(Message request)
    {
        if (request.Body is not Map body || !body.TryGetValue(SequenceNumbersBodyKey, out var seqsObj))
        {
            return BuildResponse(request, 400, "BadRequest: missing 'sequence-numbers' in body");
        }
        var seqs = ExtractLongArray(seqsObj);
        if (seqs.Length == 0)
        {
            return BuildResponse(request, 400, "BadRequest: empty 'sequence-numbers'");
        }
        foreach (var seq in seqs)
        {
            // Cancel is idempotent - silent no-op for unknown seq or already-activated messages.
            _store.TryCancelScheduledAsync(_entityName, seq).GetAwaiter().GetResult();
        }
        return BuildResponse(request, 200, "OK");
    }

    private DateTimeOffset? ComputeExpiresAt(Message msg)
    {
        TimeSpan? perMessage = msg.Header?.Ttl is uint ms and > 0
            ? TimeSpan.FromMilliseconds(ms)
            : null;
        var queueDefault = _descriptor.DefaultMessageTimeToLive;

        var effective = MinTtl(perMessage, queueDefault);
        return effective is null ? null : _timeProvider.GetUtcNow() + effective.Value;
    }

    private static TimeSpan? MinTtl(TimeSpan? a, TimeSpan? b)
    {
        if (a is null) return b;
        if (b is null) return a;
        return a < b ? a : b;
    }

    private static DateTimeOffset? ReadScheduledEnqueueTime(Message msg)
    {
        if (msg.MessageAnnotations is null) return null;
        if (!msg.MessageAnnotations.Map.TryGetValue(ScheduledEnqueueTimeSymbol, out var value)) return null;
        return value switch
        {
            DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, dt.Kind == DateTimeKind.Unspecified ? DateTimeKind.Utc : dt.Kind).ToUniversalTime()),
            DateTimeOffset dto => dto,
            _ => null,
        };
    }

    private static byte[]? ExtractMessageBytes(object value) => value switch
    {
        byte[] bytes => bytes,
        ArraySegment<byte> seg => seg.ToArray(),
        _ => null,
    };

    private static Message DecodeMessage(byte[] encoded)
    {
        var buffer = new ByteBuffer(encoded, 0, encoded.Length, encoded.Length);
        return Message.Decode(buffer);
    }

    private static string GetOperation(Message request)
    {
        if (request.ApplicationProperties is null) return string.Empty;
        return request.ApplicationProperties[OperationKey] as string ?? string.Empty;
    }

    private static Guid[] ExtractGuidArray(object value) => value switch
    {
        Guid[] guids => guids,
        object[] objs => objs.OfType<Guid>().ToArray(),
        Guid single => [single],
        _ => Array.Empty<Guid>(),
    };

    private static long[] ExtractLongArray(object value) => value switch
    {
        long[] longs => longs,
        object[] objs => objs.Select(Convert.ToInt64).ToArray(),
        System.Collections.IList list => list.Cast<object>().Select(Convert.ToInt64).ToArray(),
        long single => [single],
        _ => Array.Empty<long>(),
    };

    private static Message BuildResponse(Message request, int statusCode, string statusDescription)
    {
        var response = new Message
        {
            Properties = new Properties(),
            ApplicationProperties = new ApplicationProperties(),
        };

        var requestId = request.Properties?.GetMessageId();
        if (requestId is not null) response.Properties.SetCorrelationId(requestId);

        response.ApplicationProperties[StatusCodeKey] = statusCode;
        response.ApplicationProperties[StatusDescriptionKey] = statusDescription;
        return response;
    }
}
