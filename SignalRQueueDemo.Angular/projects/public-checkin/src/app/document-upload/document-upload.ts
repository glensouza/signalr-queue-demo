import { Component, inject, input, signal } from '@angular/core';
import { DocumentUploadResponse } from 'shared';
import { KioskCheckInService } from '../kiosk-check-in.service';

/**
 * Allowed upload content types. Mirrors <c>DocumentEndpoints.AllowedContentTypes</c> on the server so a visitor
 * gets an instant, in-place rejection instead of picking a file, waiting for an upload, and getting a 400 back.
 *
 * <p><b>The server remains the source of truth</b> — it re-validates every upload (see
 * <c>HandleUploadDocumentAsync</c>). This client copy is purely a responsiveness/UX affordance. It's kept as a
 * small explicit constant, and this comment names the server symbol it shadows, precisely so the (unavoidable)
 * duplication is easy to spot and keep in sync when the server list changes.</p>
 */
const ALLOWED_CONTENT_TYPES: readonly string[] = ['application/pdf', 'image/jpeg', 'image/png'];

/** Max upload size. Mirrors <c>DocumentEndpoints.MaxDocumentSizeBytes</c> (10 MB) — same source-of-truth note as {@link ALLOWED_CONTENT_TYPES}. */
const MAX_DOCUMENT_SIZE_BYTES: number = 10 * 1024 * 1024;

/**
 * Optional document upload against the visitor's queue entry (e.g. photographed supporting paperwork). Public and
 * unauthenticated like the rest of the kiosk flow — each upload is gated only by a fresh check-in token, obtained
 * through {@link KioskCheckInService}.
 *
 * <p>Validation (type + size) runs client-side first, mirroring the server's rules, then the server enforces them
 * again authoritatively; see {@link ALLOWED_CONTENT_TYPES} for why both sides carry the check.</p>
 */
@Component({
  selector: 'app-document-upload',
  imports: [],
  templateUrl: './document-upload.html',
  styleUrl: './document-upload.css',
})
export class DocumentUpload {
  private readonly kiosk = inject(KioskCheckInService);

  /** The queue entry these documents attach to — set by the shell once check-in succeeds. */
  readonly entryId = input.required<string>();

  protected readonly uploading = signal(false);
  protected readonly error = signal<string | null>(null);
  /** File names uploaded this session — feedback only, so the visitor sees each attachment land. Not a server read. */
  protected readonly uploaded = signal<readonly string[]>([]);

  /** Human-readable accept hint and the <input accept> value, both derived from the shared allowlist. */
  protected readonly acceptAttribute: string = ALLOWED_CONTENT_TYPES.join(',');

  /**
   * Handles a file chosen from the native picker. Validates locally, uploads, and records the result. Clears the
   * <input> value afterwards so selecting the *same* file again still fires a `change` event (the browser
   * suppresses it otherwise) — a real "re-attach the same photo" case at a kiosk.
   */
  protected async onFileSelected(event: Event): Promise<void> {
    const inputElement = event.target as HTMLInputElement;
    const file: File | undefined = inputElement.files?.[0];
    inputElement.value = '';
    if (!file) {
      return;
    }

    const validationError: string | null = validateFile(file);
    if (validationError !== null) {
      this.error.set(validationError);
      return;
    }

    this.uploading.set(true);
    this.error.set(null);
    try {
      const response: DocumentUploadResponse = await this.kiosk.uploadDocument(
        this.entryId(),
        file,
      );
      this.uploaded.update((names) => [...names, response.document.fileName]);
    } catch {
      // Same reasoning as the check-in form's catch: a visitor can't act on an HTTP status. Re-picking the file
      // retries with a fresh token, covering the most likely transient cause.
      this.error.set(`Couldn't upload “${file.name}”. Please try again.`);
    } finally {
      this.uploading.set(false);
    }
  }
}

/** Returns a visitor-facing message if the file violates the mirrored server rules, or null if it's acceptable. */
function validateFile(file: File): string | null {
  // file.type is the browser-reported MIME type — the same client-declared value the server checks against its
  // allowlist. Matching the server exactly (it does an ordinal, case-insensitive Contains) keeps the two in step.
  if (!ALLOWED_CONTENT_TYPES.some((type) => type.toLowerCase() === file.type.toLowerCase())) {
    return 'Please choose a PDF, JPEG, or PNG file.';
  }

  if (file.size === 0) {
    return 'That file is empty.';
  }

  if (file.size > MAX_DOCUMENT_SIZE_BYTES) {
    return `That file is too large. The limit is ${MAX_DOCUMENT_SIZE_BYTES / (1024 * 1024)} MB.`;
  }

  return null;
}
