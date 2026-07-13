using Microsoft.Extensions.Logging;
using SignalRQueueDemo.Contracts;
using SignalRQueueDemo.Shared.Persistence;
using SignalRQueueDemo.Shared.Persistence.Blob;

namespace SignalRQueueDemo.Shared.Documents;

/// <summary>Result of a <see cref="QueueCancellationService.CancelAsync"/> call — same shape as <see cref="QueueCompletionResult"/>, for the same reason.</summary>
public sealed record QueueCancellationResult
{
  /// <summary>The underlying <see cref="IQueueRepository.CancelAsync"/> outcome — callers branch on this exactly as they did before this extraction.</summary>
  public required QueueOperationResult OperationResult { get; init; }

  /// <summary>The document-count-cleared broadcast, populated only when cancellation succeeded and cleanup produced one. See <see cref="QueueCompletionResult.DocumentsClearedUpdate"/>'s remarks — the same "caller decides how to broadcast" split applies here.</summary>
  public QueueUpdated? DocumentsClearedUpdate { get; init; }
}

/// <summary>
/// Cancels a queue entry (the kiosk "Stop tracking" action) and cleans up its documents — the cancel counterpart
/// of <see cref="QueueCompletionService"/>, extracted for the identical reason: a visitor's supporting documents
/// exist only while they're actively in the queue, so leaving without being served should clear them exactly like
/// completing does. Without this shared extraction, Blazor's kiosk page (which calls
/// <see cref="IQueueRepository.CancelAsync"/> directly, bypassing the REST endpoint) would leak documents on
/// cancel the same way it originally leaked them on complete before <see cref="QueueCompletionService"/> existed —
/// see that type's remarks and the 2026-07-13 decisions.md entry.
/// </summary>
public sealed class QueueCancellationService(
  IQueueRepository queueRepository,
  IDocumentRepository documentRepository,
  DocumentBlobStore blobStore,
  ILogger<QueueCancellationService> logger)
{
  private readonly IQueueRepository queueRepository = queueRepository;
  private readonly IDocumentRepository documentRepository = documentRepository;
  private readonly DocumentBlobStore blobStore = blobStore;
  private readonly ILogger<QueueCancellationService> logger = logger;

  public async Task<QueueCancellationResult> CancelAsync(string entryId, CancellationToken ct = default)
  {
    QueueOperationResult result = await this.queueRepository.CancelAsync(entryId, ct);
    if (result.Outcome != QueueOperationOutcome.Success)
    {
      return new QueueCancellationResult { OperationResult = result };
    }

    // Same ordering and best-effort reasoning as QueueCompletionService.CompleteAsync: metadata first, then
    // blobs, so a partial failure can only ever orphan a harmless blob, never leave a metadata row pointing at
    // content that's already gone. Off the caller's critical path — the entry is already cancelled.
    QueueUpdated? documentsClearedUpdate = null;
    try
    {
      await this.documentRepository.DeleteDocumentsAsync(entryId, ct);
      await this.blobStore.DeleteAllForEntryAsync(entryId, ct);
      documentsClearedUpdate = await this.queueRepository.RecordDocumentChangeAsync(entryId, ct);
    }
    catch (Exception ex)
    {
      this.logger.LogWarning(
        ex, "Cancelled entry {EntryId} but failed to delete its documents; content may be orphaned in Blob Storage.", entryId);
    }

    return new QueueCancellationResult { OperationResult = result, DocumentsClearedUpdate = documentsClearedUpdate };
  }
}
