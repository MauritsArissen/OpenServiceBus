using OpenServiceBus.Core.Entities;

namespace OpenServiceBus.Core.Storage;

/// <summary>
/// Tracks queue entities and emits lifecycle events the AMQP listener subscribes to
/// so it can register/unregister per-entity link handlers without coupling the broker
/// to AMQP types directly.
/// </summary>
public interface IQueueRegistry
{
    /// <summary>Create a queue. No-op if it already exists (caller decides on conflict policy).</summary>
    Task<QueueDescriptor> CreateAsync(QueueDescriptor descriptor, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replace the descriptor of an existing queue in place - messages are untouched. The caller
    /// is responsible for rejecting changes to create-time-only properties (sessions, duplicate
    /// detection). Throws <see cref="InvalidOperationException"/> when the queue does not exist.
    /// </summary>
    Task<QueueDescriptor> UpdateAsync(QueueDescriptor descriptor, CancellationToken cancellationToken = default);

    Task<QueueDescriptor?> GetAsync(string name, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<QueueDescriptor>> ListAsync(CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Raised after a new queue has been created and its storage allocated.</summary>
    event EventHandler<QueueDescriptor> QueueCreated;

    /// <summary>Raised after a queue's descriptor has been replaced via <see cref="UpdateAsync"/>.</summary>
    event EventHandler<QueueDescriptor> QueueUpdated;

    /// <summary>Raised after a queue has been deleted.</summary>
    event EventHandler<QueueDescriptor> QueueDeleted;
}
