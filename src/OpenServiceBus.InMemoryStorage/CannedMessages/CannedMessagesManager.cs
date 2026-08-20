using System.Collections.Concurrent;
using OpenServiceBus.Core.Entities;
using OpenServiceBus.Core.Storage;

namespace OpenServiceBus.InMemoryStorage.CannedMessages;

public sealed class CannedMessagesManager : ICannedMessagesRegistry
{
    private readonly IMessageStore _store;
    private readonly ConcurrentDictionary<string, CannedMessage> _cannedMessages = new(StringComparer.OrdinalIgnoreCase);

    public CannedMessagesManager(IMessageStore store)
    {
        _store = store;
    }

    public async Task<CannedMessage> CreateAsync(CannedMessage cannedMessage, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cannedMessage);
        ArgumentException.ThrowIfNullOrWhiteSpace(cannedMessage.Name);

        var existing = _cannedMessages.GetOrAdd(cannedMessage.Name, cannedMessage);
        if (!ReferenceEquals(existing, cannedMessage))
        {
            // Already existed - idempotent create.
            return existing;
        }

        await _store.CreateCannedMessageAsync(cannedMessage.Name, cannedMessage, cancellationToken).ConfigureAwait(false);
        ////await _store.SaveQueueDescriptorAsync(descriptor.Name, QueueDescriptorJson.Serialize(descriptor), cancellationToken).ConfigureAwait(false);
        
        return cannedMessage;
    }

    public async Task<CannedMessage> UpdateAsync(CannedMessage cannedMessage, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cannedMessage);
        ArgumentException.ThrowIfNullOrWhiteSpace(cannedMessage.Name);

        var existing = _cannedMessages.GetOrAdd(cannedMessage.Name, cannedMessage);
        if (!ReferenceEquals(existing, cannedMessage))
        {
            // Already existed - idempotent create.
            return existing;
        }

        await _store.CreateCannedMessageAsync(cannedMessage.Name, cannedMessage, cancellationToken).ConfigureAwait(false);
        ////await _store.SaveQueueDescriptorAsync(descriptor.Name, QueueDescriptorJson.Serialize(descriptor), cancellationToken).ConfigureAwait(false);

        return cannedMessage;
    }

    public Task<CannedMessage?> GetAsync(string name, CancellationToken cancellationToken = default)
    {
        _cannedMessages.TryGetValue(name, out var descriptor);
        return Task.FromResult(descriptor);
    }

    public Task<IReadOnlyList<CannedMessage>> ListAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<CannedMessage> snapshot = _cannedMessages.Values.ToArray();
        return Task.FromResult(snapshot);
    }

    public async Task<bool> DeleteAsync(string name, CancellationToken cancellationToken = default)
    {
        if (!_cannedMessages.TryRemove(name, out var descriptor))
        {
            return false;
        }
        await _store.DeleteQueueAsync(name, cancellationToken).ConfigureAwait(false);

        return true;
    }
}
