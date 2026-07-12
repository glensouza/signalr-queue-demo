import { HttpErrorResponse } from '@angular/common/http';
import { Component, OnDestroy, computed, effect, inject, input, signal } from '@angular/core';
import { DomSanitizer, SafeResourceUrl } from '@angular/platform-browser';
import { DocumentListResponse, DocumentMetadata, QueueApiService } from 'shared';

/**
 * Document list and inline viewer for a selected queue entry. Staff can view uploaded supporting documents
 * (receipts, photos, etc.) that a visitor attached during check-in or while waiting.
 *
 * <p><b>Object URL lifecycle management:</b> This component fetches documents as Blobs and builds object URLs
 * via {@link URL.createObjectURL} for inline display. Object URLs hold references to Blob data and must be
 * revoked when no longer needed (see {@link URL.revokeObjectURL}), or the browser will retain memory indefinitely.
 * Revocation happens:
 * <ul>
 *   <li>When the user selects a different document,
 *   <li>When the viewed entry changes,
 *   <li>In {@link ngOnDestroy}, if the component is torn down while a document is still open.
 * </ul>
 * Forgetting any of these is a real memory leak on a long-running staff console left open all day.</p>
 *
 * <p><b>Why the endpoint requires the `X-Staff-Key` header:</b> All document content is gated by
 * {@link StaffAuthFilter} on the backend, so a plain `<img src>` or `<a href>` can't fetch it — only an
 * authenticated HTTP request with the header can. This component fetches the Blob client-side and builds an
 * object URL to display it.</p>
 *
 * <p><b>PDF vs. image display:</b> Images (`image/jpeg`, `image/png`) render in an `<img>` tag; PDFs render in
 * an `<iframe>`, letting the browser's native PDF viewer display it. The object URL is the transport for both,
 * but they bind it differently: an `<img src>` is a URL security context, where Angular accepts a `blob:` string
 * as-is, whereas an `<iframe src>` is a *resource*-URL context, which Angular refuses to bind a raw string to
 * (it would throw "unsafe value used in a resource URL context"). So the iframe binds a {@link SafeResourceUrl}
 * produced via {@link DomSanitizer.bypassSecurityTrustResourceUrl} — safe here precisely because the URL is one
 * we just minted from a Blob we fetched ourselves, not attacker-controlled input.</p>
 */
@Component({
  selector: 'app-document-viewer',
  imports: [],
  templateUrl: './document-viewer.html',
  styleUrl: './document-viewer.css',
})
export class DocumentViewer implements OnDestroy {
  private readonly api = inject(QueueApiService);
  private readonly sanitizer = inject(DomSanitizer);

  /** The queue entry id whose documents to list. */
  readonly entryId = input.required<string>();

  /** The staff key for authenticated requests. */
  readonly staffKey = input.required<string>();

  /** List of documents for the entry, or null while loading. */
  protected readonly documents = signal<DocumentMetadata[] | null>(null);

  /** Id of the currently selected document for viewing, or null if none selected. */
  protected readonly selectedDocId = signal<string | null>(null);

  /** The selected document's content as a Blob, or null if not fetched yet. */
  protected readonly selectedBlob = signal<Blob | null>(null);

  /** The raw object URL of the selected Blob, bound to an `<img src>` (a URL context, which takes a plain string). Null if no document is selected. */
  protected readonly selectedObjectUrl = signal<string | null>(null);

  /** The same object URL wrapped as a {@link SafeResourceUrl} for the `<iframe src>` PDF path — see the class remarks on why the iframe can't take the raw string. */
  protected readonly selectedSafeUrl = signal<SafeResourceUrl | null>(null);

  /**
   * The object URL that currently needs revoking, held as a plain field rather than read back off
   * {@link selectedObjectUrl}. This is deliberate: {@link revokeSelectedObjectUrl} runs *inside*
   * {@link createObjectUrlOnBlobChange}, and if it read the `selectedObjectUrl` signal there, that read would make
   * the signal a dependency of an effect that also writes it — a self-retriggering loop that would revoke each URL
   * the instant it was created. A plain field is invisible to signal tracking, so revocation bookkeeping can't
   * feed back into the effect graph.
   */
  private currentObjectUrl: string | null = null;

  /** Loading state: true while fetching the document list or a selected document's content. */
  protected readonly loading = signal(false);

  /** Error message from the last failed load (list or document fetch), or null if successful. */
  protected readonly error = signal<string | null>(null);

  /** The metadata of the currently selected document, or null if none — resolved from the list so the template needn't re-scan it inline. */
  protected readonly selectedDoc = computed<DocumentMetadata | null>(() => {
    const docId = this.selectedDocId();
    const docs = this.documents();
    if (docId === null || docs === null) {
      return null;
    }

    return docs.find((doc) => doc.id === docId) ?? null;
  });

  /** True when the selected document is a PDF (rendered via `<iframe>`), false for images (rendered via `<img>`). */
  protected readonly selectedIsPdf = computed<boolean>(
    () => this.selectedDoc()?.contentType === 'application/pdf',
  );

  /**
   * Formats bytes to a human-readable size (e.g. "1.5 MB"). Used in the document list.
   */
  protected readonly formatFileSize = (bytes: number): string => {
    if (bytes < 1024) return `${bytes} B`;
    if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
    return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
  };

  /**
   * Effect: whenever the entry id changes, load its document list. This clears any previously selected document
   * and revokes any open object URL.
   */
  private readonly loadDocumentsOnEntryChange = effect(() => {
    this.selectedDocId.set(null);
    this.revokeSelectedObjectUrl();
    this.loadDocuments(this.entryId());
  });

  /**
   * Effect: whenever the selected document changes, fetch its content if a new document is selected.
   */
  private readonly fetchDocumentOnSelection = effect(() => {
    const docId = this.selectedDocId();
    if (docId === null) {
      this.selectedBlob.set(null);
      this.revokeSelectedObjectUrl();
      return;
    }

    this.loadDocument(docId);
  });

  /**
   * Effect: whenever the selected Blob changes, create a new object URL and revoke the old one.
   */
  private readonly createObjectUrlOnBlobChange = effect(() => {
    const blob = this.selectedBlob();
    this.revokeSelectedObjectUrl();

    if (blob !== null) {
      const url = URL.createObjectURL(blob);
      this.currentObjectUrl = url;
      this.selectedObjectUrl.set(url);
      this.selectedSafeUrl.set(this.sanitizer.bypassSecurityTrustResourceUrl(url));
    }
  });

  ngOnDestroy(): void {
    this.revokeSelectedObjectUrl();
  }

  protected selectDocument(docId: string): void {
    this.selectedDocId.set(docId);
  }

  private async loadDocuments(entryId: string): Promise<void> {
    this.documents.set(null);
    this.loading.set(true);
    this.error.set(null);

    try {
      const response = await new Promise<DocumentListResponse>((resolve, reject) => {
        this.api
          .listDocuments(entryId, this.staffKey())
          .subscribe({ next: resolve, error: reject });
      });

      this.documents.set([...response.documents]);
    } catch (err) {
      this.error.set(errorMessage(err, 'Failed to load documents. Please try again.'));
      this.documents.set([]);
    } finally {
      this.loading.set(false);
    }
  }

  private async loadDocument(docId: string): Promise<void> {
    this.selectedBlob.set(null);
    this.loading.set(true);
    this.error.set(null);

    try {
      const blob = await new Promise<Blob>((resolve, reject) => {
        this.api
          .getDocument(this.entryId(), docId, this.staffKey())
          .subscribe({ next: resolve, error: reject });
      });

      this.selectedBlob.set(blob);
    } catch (err) {
      this.error.set(errorMessage(err, 'Failed to load document. Please try again.'));
    } finally {
      this.loading.set(false);
    }
  }

  private revokeSelectedObjectUrl(): void {
    if (this.currentObjectUrl !== null) {
      URL.revokeObjectURL(this.currentObjectUrl);
      this.currentObjectUrl = null;
    }

    this.selectedObjectUrl.set(null);
    this.selectedSafeUrl.set(null);
  }
}

/**
 * Maps a failed document request to a staff-facing message. A 401 gets a "sign in again" hint; everything else
 * gets the caller's generic retry message. Checks {@link HttpErrorResponse.status} rather than the error's
 * message text because Angular's `HttpErrorResponse` is not a subclass of `Error`, so a message-substring test
 * silently never matches the status code.
 */
function errorMessage(err: unknown, fallback: string): string {
  if (err instanceof HttpErrorResponse && err.status === 401) {
    return 'Your staff key is invalid. Please sign in again.';
  }

  return fallback;
}
