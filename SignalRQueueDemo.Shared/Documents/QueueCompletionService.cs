using Microsoft.Extensions.Logging;
using SignalRQueueDemo.Contracts;
using SignalRQueueDemo.Shared.Persistence;
using SignalRQueueDemo.Shared.Persistence.Blob;

namespace SignalRQueueDemo.Shared.Documents;

/// <summary>Result of a <see cref="QueueCompletionService.CompleteAsync"/> call.</summary>
public sealed record QueueCompletionResult
{
  /// <summary>The underlying <see cref="IQueueRepository.CompleteAsync"/> outcome — callers branch on this exactly as they did before this extraction.</summary>
  public required QueueOperationResult OperationResult { get; init; }

  /// <summary>
  /// The document-count-cleared broadcast, populated only when completion succeeded and the best-effort document
  /// cleanup below produced one. Deliberately not published by this service itself (same reasoning as
  /// <see cref="DocumentUploadService"/>'s <c>Update</c>) — the caller decides how: <c>QueueBroadcaster.BroadcastAsync</c>
  /// directly in ApiService's own process, or Blazor's <c>QueueRealtimeService.PublishAsync</c> (a hub round-trip)
  /// from Web's.
  /// </summary>
  public QueueUpdated? DocumentsClearedUpdate { get; init; }
}

/// <summary>
/// Completes a queue entry and cleans up its documents — moved out of
/// <c>SignalRQueueDemo.ApiService.Endpoints.QueueEndpoints.HandleCompleteAsync</c> so Blazor's staff console (which
/// calls <see cref="IQueueRepository.CompleteAsync"/> directly, bypassing that REST endpoint entirely per the
/// "Blazor is self-encapsulated" decision) also deletes a completed entry's documents instead of leaking them.
/// Before this extraction, only the REST path deleted documents on complete — a Blazor-completed entry kept its
/// blobs and metadata forever, silently violating the documented "documents live only while the visitor is in the
/// queue" lifecycle. See <c>DocumentUploadService</c>'s remarks for why this kind of logic belongs in
/// <c>SignalRQueueDemo.Shared</c> rather than duplicated per host.
/// </summary>
public sealed class QueueCompletionService(
  IQueueRepository queueRepository,
  IDocumentRepository documentRepository,
  DocumentBlobStore blobStore,
  ILogger<QueueCompletionService> logger)
{
  private readonly IQueueRepository queueRepository = queueRepository;
  private readonly IDocumentRepository documentRepository = documentRepository;
  private readonly DocumentBlobStore blobStore = blobStore;
  private readonly ILogger<QueueCompletionService> logger = logger;

  public async Task<QueueCompletionResult> CompleteAsync(string entryId, CancellationToken ct = default)
  {
    QueueOperationResult result = await this.queueRepository.CompleteAsync(entryId, ct);
    if (result.Outcome != QueueOperationOutcome.Success)
    {
      return new QueueCompletionResult { OperationResult = result };
    }

    // A visitor's supporting documents exist only to be reviewed while they're in the queue; once completed
    // there's no reason to keep them, so this deletes both halves. Metadata FIRST, then blobs: a failure partway
    // can then only ever leave an orphaned blob (harmless, cleanable) — never a metadata row pointing at content
    // that's already gone, which is the very "content missing" failure this whole cleanup exists to avoid.
    // Best-effort and off the caller's critical path: the entry is already completed, so a cleanup hiccup logs
    // and moves on rather than turning a successful complete into an error the caller would retry.
    QueueUpdated? documentsClearedUpdate = null;
    try
    {
      await this.documentRepository.DeleteDocumentsAsync(entryId, ct);
      await this.blobStore.DeleteAllForEntryAsync(entryId, ct);

      // The completion Update (result.OperationResult.Update) necessarily precedes this deletion, so it still
      // carries the entry's pre-deletion DocumentCount. This second, document-only update is what lets connected
      // clients see DocumentCount drop to 0 instead of staying stale at its last-known value forever.
      documentsClearedUpdate = await this.queueRepository.RecordDocumentChangeAsync(entryId, ct);
    }
    catch (Exception ex)
    {
      this.logger.LogWarning(
        ex, "Completed entry {EntryId} but failed to delete its documents; content may be orphaned in Blob Storage.", entryId);
    }

    return new QueueCompletionResult { OperationResult = result, DocumentsClearedUpdate = documentsClearedUpdate };
  }
}
