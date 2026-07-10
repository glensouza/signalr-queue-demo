using Microsoft.AspNetCore.SignalR;
using SignalRQueueDemo.Contracts;

namespace SignalRQueueDemo.ApiService.Hubs;

/// <summary>
/// The single choke point every queue mutation pushes its <see cref="QueueUpdated"/> through. Two reasons it
/// exists rather than each endpoint calling <see cref="IHubContext{THub, T}"/> directly:
///
/// <para>
/// <b>One place to change how broadcasting works.</b> All three mutations (check-in, call-next, complete)
/// broadcast identically. Group/target filtering when CORS + the trust boundary land (issue #6), switching to
/// the Azure SignalR path (issue #7), or adding logging happens here once instead of in three handlers where
/// one could be missed.
/// </para>
///
/// <para>
/// <b>A broadcast can never fail an already-committed mutation.</b> The write is durable before we get here, so
/// if the push throws (a faulted SignalR pipeline today, or a network send to the Azure SignalR service under
/// issue #7) we log and swallow rather than let it propagate. Letting it bubble would turn a successful check-in
/// into an HTTP 500 whose caller retries — double-checking-in a visitor, or skipping a waiting person on a
/// retried call-next. ADR-0001's "a notification must never silently drop" is honored not by failing the caller
/// but by the catch-up protocol: the committed change is replayed to any client via GET /queue/since/{seq}.
/// </para>
/// </summary>
public sealed class QueueBroadcaster(
  IHubContext<QueueHub, IQueueHubClient> hubContext,
  ILogger<QueueBroadcaster> logger)
{
  private readonly IHubContext<QueueHub, IQueueHubClient> hubContext = hubContext;
  private readonly ILogger<QueueBroadcaster> logger = logger;

  /// <summary>
  /// Pushes <paramref name="update"/> to every connected client. Never throws: a failed push is logged and the
  /// caller continues, because the mutation the update describes is already committed (see type remarks).
  /// </summary>
  public async Task BroadcastAsync(QueueUpdated update)
  {
    try
    {
      await this.hubContext.Clients.All.QueueUpdated(update);
    }
    catch (Exception ex)
    {
      this.logger.LogError(
        ex,
        "Broadcasting QueueUpdated for sequence {SequenceNumber} failed. The change is committed; clients " +
        "will catch up via GET /queue/since/{{sequenceNumber}} on their next reconnect.",
        update.SequenceNumber);
    }
  }
}
