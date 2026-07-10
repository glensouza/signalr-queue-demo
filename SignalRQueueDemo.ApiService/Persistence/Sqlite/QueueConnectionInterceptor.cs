using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace SignalRQueueDemo.ApiService.Persistence.Sqlite;

/// <summary>
/// Applies the connection-level PRAGMAs the SQLite queue store needs for safe concurrent access, on every
/// physical connection open. Done as an interceptor rather than a one-time startup call because
/// <c>busy_timeout</c> is per-connection (it isn't stored in the file), so a single call at startup would be
/// silently lost on every pooled connection opened thereafter.
/// </summary>
/// <remarks>
/// Without <c>busy_timeout</c>, Microsoft.Data.Sqlite fails a write that collides with another in-flight
/// writer immediately with "database is locked" (SQLITE_BUSY), which surfaces as an unhandled HTTP 500. The
/// court demo deliberately drives concurrent writes — a kiosk checking in while staff calls the next entry —
/// so waiting briefly for the other writer to finish, rather than erroring, is required, not optional.
/// <c>journal_mode=WAL</c> additionally lets a reader (the queue-display board polling GET /queue) proceed
/// without blocking the writer.
/// </remarks>
public sealed class QueueConnectionInterceptor : DbConnectionInterceptor
{
  /// <summary>Applies the PRAGMAs after a synchronous connection open.</summary>
  public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData) =>
    ApplyPragmas(connection);

  /// <summary>Applies the PRAGMAs after an asynchronous connection open.</summary>
  public override Task ConnectionOpenedAsync(
    DbConnection connection,
    ConnectionEndEventData eventData,
    CancellationToken cancellationToken = default)
  {
    ApplyPragmas(connection);
    return Task.CompletedTask;
  }

  private static void ApplyPragmas(DbConnection connection)
  {
    using DbCommand command = connection.CreateCommand();
    // 5s is generous at this scale (writes are sub-millisecond) yet still surfaces a genuine deadlock instead
    // of hanging forever. journal_mode=WAL persists in the file, so re-setting it per open is a harmless no-op.
    command.CommandText = "PRAGMA busy_timeout = 5000; PRAGMA journal_mode = WAL;";
    command.ExecuteNonQuery();
  }
}
