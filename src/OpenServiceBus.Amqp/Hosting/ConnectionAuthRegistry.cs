using System.Runtime.CompilerServices;
using Amqp;

namespace OpenServiceBus.Amqp.Hosting;

/// <summary>
/// Tracks which AMQP connections have presented a valid SAS token over <c>$cbs</c>. Used
/// only when <see cref="AmqpListenerOptions.RequireSasAuth"/> is enabled: the CBS handler
/// marks a connection authenticated on a successful <c>put-token</c>, and the link
/// processor refuses entity/management attaches on connections that never did so - closing
/// the gap where a raw client that skips <c>$cbs</c> would otherwise get full access.
///
/// Keyed on the <see cref="Connection"/> instance via a <see cref="ConditionalWeakTable{TKey,TValue}"/>
/// so entries are collected automatically when a connection is torn down - no eviction pass
/// and no leak on connection churn.
/// </summary>
public sealed class ConnectionAuthRegistry
{
    private static readonly object Marker = new();
    private readonly ConditionalWeakTable<Connection, object> _authenticated = new();

    /// <summary>Record that <paramref name="connection"/> has authenticated successfully.</summary>
    public void MarkAuthenticated(Connection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _authenticated.AddOrUpdate(connection, Marker);
    }

    /// <summary>True once <paramref name="connection"/> has presented a valid token.</summary>
    public bool IsAuthenticated(Connection? connection) =>
        connection is not null && _authenticated.TryGetValue(connection, out _);
}
