using Amqp;
using Amqp.Framing;
using Amqp.Listener;
using Amqp.Types;
using Microsoft.Extensions.Logging;

namespace OpenServiceBus.Amqp.Routing;

internal static class DeletedEntityLink
{
    public static void Close(ListenerLink link, string entityName, ILogger logger)
    {
        if (link.IsClosed) return;
        try
        {
            link.Close(TimeSpan.Zero, new Error(new Symbol(ErrorCode.NotFound))
            {
                Description = $"The messaging entity '{entityName}' has been deleted.",
                Info = new Fields(),
            });
            logger.LogDebug("Detached link on deleted entity {Entity}", entityName);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to detach link on deleted entity {Entity}", entityName);
        }
    }
}
