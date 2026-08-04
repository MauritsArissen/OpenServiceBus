using System.Collections.Concurrent;
using OpenServiceBus.Core.Entities;
using OpenServiceBus.Core.Messaging;
using OpenServiceBus.Core.Storage;

namespace OpenServiceBus.InMemoryStorage.Queues;

public sealed class QueueManager : IQueueRegistry
{
    /// <summary>The suffix Service Bus uses for the dead-letter sub-entity of a queue.</summary>
    public const string DeadLetterSuffix = "/$DeadLetterQueue";

    private readonly IMessageStore _store;
    private readonly ConcurrentDictionary<string, QueueDescriptor> _queues = new(StringComparer.OrdinalIgnoreCase);

    public QueueManager(IMessageStore store)
    {
        _store = store;
    }

    public event EventHandler<QueueDescriptor>? QueueCreated;
    public event EventHandler<QueueDescriptor>? QueueUpdated;
    public event EventHandler<QueueDescriptor>? QueueDeleted;

    event EventHandler<QueueDescriptor> IQueueRegistry.QueueCreated
    {
        add => QueueCreated += value;
        remove => QueueCreated -= value;
    }

    event EventHandler<QueueDescriptor> IQueueRegistry.QueueUpdated
    {
        add => QueueUpdated += value;
        remove => QueueUpdated -= value;
    }

    event EventHandler<QueueDescriptor> IQueueRegistry.QueueDeleted
    {
        add => QueueDeleted += value;
        remove => QueueDeleted -= value;
    }

    /// <summary>
    /// Returns true when <paramref name="name"/> identifies a dead-letter sub-entity (e.g. <c>orders/$DeadLetterQueue</c>).
    /// DLQ entities do not themselves get DLQ siblings and have effectively unbounded redelivery to prevent loops.
    /// </summary>
    public static bool IsDeadLetterQueue(string name) =>
        name.EndsWith(DeadLetterSuffix, StringComparison.Ordinal);

    public async Task<QueueDescriptor> CreateAsync(QueueDescriptor descriptor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptor.Name);

        // Reject obviously broken auto-forward configs. We only do the cheap self-equality
        // check at creation; target-exists is checked lazily by the router at runtime - that lets
        // config.json bootstrap entities in arbitrary order without forcing a topological sort.
        if (!string.IsNullOrEmpty(descriptor.ForwardTo) && string.Equals(descriptor.ForwardTo, descriptor.Name, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Queue '{descriptor.Name}' cannot forward to itself.");
        }
        if (!string.IsNullOrEmpty(descriptor.ForwardDeadLetteredMessagesTo) && string.Equals(descriptor.ForwardDeadLetteredMessagesTo, descriptor.Name, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Queue '{descriptor.Name}' cannot forward dead-lettered messages to itself.");
        }

        var existing = _queues.GetOrAdd(descriptor.Name, descriptor);
        if (!ReferenceEquals(existing, descriptor))
        {
            // Already existed - idempotent create.
            return existing;
        }

        await _store.CreateQueueAsync(descriptor.Name, cancellationToken).ConfigureAwait(false);
        await _store.SaveQueueDescriptorAsync(descriptor.Name, QueueDescriptorJson.Serialize(descriptor), cancellationToken).ConfigureAwait(false);
        QueueCreated?.Invoke(this, descriptor);

        // Every main queue gets an implicit dead-letter sibling - Azure Service Bus's contract.
        // DLQs themselves do NOT get DLQ siblings (no recursion).
        if (!IsDeadLetterQueue(descriptor.Name))
        {
            var dlq = new QueueDescriptor
            {
                Name = descriptor.Name + DeadLetterSuffix,
                LockDuration = descriptor.LockDuration,
                MaxDeliveryCount = int.MaxValue,
            };
            await CreateAsync(dlq, cancellationToken).ConfigureAwait(false);
        }

        return descriptor;
    }

    public async Task<QueueDescriptor> UpdateAsync(QueueDescriptor descriptor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptor.Name);

        if (!string.IsNullOrEmpty(descriptor.ForwardTo) && string.Equals(descriptor.ForwardTo, descriptor.Name, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Queue '{descriptor.Name}' cannot forward to itself.");
        }
        if (!string.IsNullOrEmpty(descriptor.ForwardDeadLetteredMessagesTo) && string.Equals(descriptor.ForwardDeadLetteredMessagesTo, descriptor.Name, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Queue '{descriptor.Name}' cannot forward dead-lettered messages to itself.");
        }

        // Swap loop: keep the stored key's identity (create-time name) and only replace when the
        // queue still exists at the moment of the swap - a concurrent delete must win, not be
        // resurrected by a racing update.
        while (true)
        {
            if (!_queues.TryGetValue(descriptor.Name, out var existing))
            {
                throw new InvalidOperationException($"Queue '{descriptor.Name}' does not exist.");
            }
            if (_queues.TryUpdate(descriptor.Name, descriptor, existing))
            {
                break;
            }
        }

        await _store.SaveQueueDescriptorAsync(descriptor.Name, QueueDescriptorJson.Serialize(descriptor), cancellationToken).ConfigureAwait(false);
        QueueUpdated?.Invoke(this, descriptor);

        // Keep the DLQ sibling's lock duration in step with the parent, mirroring create.
        if (!IsDeadLetterQueue(descriptor.Name)
            && _queues.TryGetValue(descriptor.Name + DeadLetterSuffix, out var dlq)
            && dlq.LockDuration != descriptor.LockDuration)
        {
            await UpdateAsync(dlq with { LockDuration = descriptor.LockDuration }, cancellationToken).ConfigureAwait(false);
        }

        return descriptor;
    }

    public Task<QueueDescriptor?> GetAsync(string name, CancellationToken cancellationToken = default)
    {
        _queues.TryGetValue(name, out var descriptor);
        return Task.FromResult(descriptor);
    }

    public Task<IReadOnlyList<QueueDescriptor>> ListAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<QueueDescriptor> snapshot = _queues.Values.ToArray();
        return Task.FromResult(snapshot);
    }

    public async Task<bool> DeleteAsync(string name, CancellationToken cancellationToken = default)
    {
        if (!_queues.TryRemove(name, out var descriptor))
        {
            return false;
        }
        await _store.DeleteQueueAsync(name, cancellationToken).ConfigureAwait(false);
        QueueDeleted?.Invoke(this, descriptor);

        // Tear down the DLQ sibling alongside the main queue.
        if (!IsDeadLetterQueue(name))
        {
            await DeleteAsync(name + DeadLetterSuffix, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }
}
