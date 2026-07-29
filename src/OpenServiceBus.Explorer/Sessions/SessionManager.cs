using System.Collections.Concurrent;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;

namespace OpenServiceBus.Explorer.Sessions;

/// <summary>
/// Pools <see cref="ServiceBusClient"/> and per-queue <see cref="ServiceBusReceiver"/> instances
/// keyed by connection string. Peek-lock dispositions (Complete/Abandon/DeadLetter) require the
/// same receiver instance that originally received the message, so we keep receivers alive across
/// HTTP requests.
/// </summary>
public sealed class SessionManager : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, Session> _sessions = new();

    public Session GetOrCreate(string connectionString)
    {
        return _sessions.GetOrAdd(connectionString, cs => new Session(cs));
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var session in _sessions.Values)
        {
            await session.DisposeAsync();
        }
        _sessions.Clear();
    }
}

public sealed class Session : IAsyncDisposable
{
    private readonly string _connectionString;
    private readonly ServiceBusClient _client;
    private ServiceBusAdministrationClient? _admin;
    private readonly ConcurrentDictionary<string, ServiceBusReceiver> _receivers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (ServiceBusReceivedMessage Message, ServiceBusReceiver Receiver)> _lockedMessages = new(StringComparer.OrdinalIgnoreCase);

    internal Session(string connectionString)
    {
        _connectionString = connectionString;
        _client = new ServiceBusClient(connectionString);
    }

    /// <summary>
    /// The real SDK admin client for this connection - entity CRUD goes through the broker's
    /// ATOM management API, the same protocol every other Service Bus admin client speaks.
    /// Fail-fast retry: the Explorer is interactive, a dead endpoint should error in seconds.
    /// </summary>
    public ServiceBusAdministrationClient Admin => _admin ??= new ServiceBusAdministrationClient(
        _connectionString,
        new ServiceBusAdministrationClientOptions
        {
            Retry = { MaxRetries = 1, NetworkTimeout = TimeSpan.FromSeconds(10) },
        });

    public ServiceBusSender Sender(string queueOrTopic) => _client.CreateSender(queueOrTopic);

    /// <summary>
    /// Get (or create) a peek-lock receiver for the entity address. Subscription paths of the
    /// form <c>&lt;topic&gt;/Subscriptions/&lt;sub&gt;</c> are split into the SDK's two-arg
    /// overload so the AMQP attach lands on the subscription backing queue correctly.
    /// </summary>
    public ServiceBusReceiver Receiver(string queueOrSubscription) => _receivers.GetOrAdd(queueOrSubscription, q =>
    {
        var options = new ServiceBusReceiverOptions
        {
            ReceiveMode = ServiceBusReceiveMode.PeekLock,
            PrefetchCount = 0,
        };
        const string segment = "/Subscriptions/";
        var idx = q.IndexOf(segment, StringComparison.OrdinalIgnoreCase);
        if (idx > 0)
        {
            var topic = q[..idx];
            var sub = q[(idx + segment.Length)..];
            // The Azure SDK expects bare topic + sub names (no DLQ suffix). If the receiver
            // is for the subscription's DLQ, pass it via the SubQueue option instead.
            const string dlq = "/$DeadLetterQueue";
            if (sub.EndsWith(dlq, StringComparison.Ordinal))
            {
                sub = sub[..^dlq.Length];
                options.SubQueue = SubQueue.DeadLetter;
            }
            return _client.CreateReceiver(topic, sub, options);
        }
        return _client.CreateReceiver(q, options);
    });

    /// <summary>
    /// Get (or create) a session-locked receiver for a specific session id on a queue or
    /// subscription. Same address shape as <see cref="Receiver(string)"/>; the session lock
    /// stays alive across HTTP requests for the duration of this Explorer Session.
    /// </summary>
    public async Task<ServiceBusSessionReceiver> SessionReceiverAsync(string queueOrSubscription, string sessionId)
    {
        var key = $"{queueOrSubscription}|session={sessionId}";
        if (_receivers.TryGetValue(key, out var existing) && existing is ServiceBusSessionReceiver sessionReceiver)
        {
            return sessionReceiver;
        }
        ServiceBusSessionReceiver created;
        const string segment = "/Subscriptions/";
        var idx = queueOrSubscription.IndexOf(segment, StringComparison.OrdinalIgnoreCase);
        if (idx > 0)
        {
            var topic = queueOrSubscription[..idx];
            var sub = queueOrSubscription[(idx + segment.Length)..];
            created = await _client.AcceptSessionAsync(topic, sub, sessionId);
        }
        else
        {
            created = await _client.AcceptSessionAsync(queueOrSubscription, sessionId);
        }
        _receivers[key] = created;
        return created;
    }

    /// <summary>
    /// Stash a received message so a later Complete/Abandon call can find it by lock-token.
    /// Remembers the receiver instance - for session messages the same session-receiver must
    /// be used to settle the message; using a plain receiver would surface a lock-lost error.
    /// </summary>
    public void TrackLocked(ServiceBusReceivedMessage message, ServiceBusReceiver receiver)
    {
        _lockedMessages[message.LockToken] = (message, receiver);
    }

    public bool TryTakeLocked(string lockToken, out ServiceBusReceivedMessage? message, out ServiceBusReceiver? receiver)
    {
        var found = _lockedMessages.TryRemove(lockToken, out var entry);
        message = entry.Message;
        receiver = entry.Receiver;
        return found;
    }

    /// <summary>Look up a tracked locked message without removing it (used for RenewLock).</summary>
    public bool TryPeekLocked(string lockToken, out ServiceBusReceivedMessage? message, out ServiceBusReceiver? receiver)
    {
        var found = _lockedMessages.TryGetValue(lockToken, out var entry);
        message = entry.Message;
        receiver = entry.Receiver;
        return found;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var receiver in _receivers.Values)
        {
            try { await receiver.DisposeAsync(); } catch { /* best effort */ }
        }
        _receivers.Clear();
        _lockedMessages.Clear();
        await _client.DisposeAsync();
    }
}
