/**
 * TypeScript mirrors of SignalRQueueDemo.Contracts/DocumentDomainTypes.cs — update together. See
 * queue.models.ts's file header for why property names are camelCase, not PascalCase.
 */

/** Mirrors SignalRQueueDemo.Contracts.DocumentMetadata. Never carries the storage blob name — see the C# type's remarks. */
export interface DocumentMetadata {
  readonly id: string;
  readonly entryId: string;
  readonly fileName: string;
  readonly contentType: string;
  readonly sizeBytes: number;
  /** ISO 8601 string — see QueueEntry.checkedInAt for why this stays a string, not a parsed Date, in the wire-shape mirror. */
  readonly uploadedAt: string;
}

/** Mirrors SignalRQueueDemo.Contracts.DocumentUploadResponse — the response to POST /checkin/{id}/documents. */
export interface DocumentUploadResponse {
  readonly document: DocumentMetadata;
}

/** Mirrors SignalRQueueDemo.Contracts.DocumentListResponse — the response to GET /queue/{id}/documents. */
export interface DocumentListResponse {
  readonly documents: readonly DocumentMetadata[];
}
