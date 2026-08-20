using OpenServiceBus.Core.Entities;

namespace OpenServiceBus.Core.Storage;

/// <summary>
/// Tracks canned messages entities.
/// </summary>
public interface ICannedMessagesRegistry
{
    Task<CannedMessage> CreateAsync(CannedMessage cannedMessage, CancellationToken cancellationToken = default);

    Task<CannedMessage> UpdateAsync(CannedMessage cannedMessage, CancellationToken cancellationToken = default);

    Task<CannedMessage?> GetAsync(string name, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CannedMessage>> ListAsync(CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(string name, CancellationToken cancellationToken = default);
}
