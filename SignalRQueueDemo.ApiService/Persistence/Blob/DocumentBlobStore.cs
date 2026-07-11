using System.Net;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace SignalRQueueDemo.ApiService.Persistence.Blob;

/// <summary>
/// Blob Storage access for uploaded document *content* — document *metadata* lives in
/// <see cref="IDocumentRepository"/>, alongside the queue entry it belongs to (see that interface's remarks for
/// why the two are split). Always targets the Azurite emulator regardless of <c>Persistence:Provider</c>:
/// unlike <see cref="IQueueRepository"/>, Blob Storage isn't a swappable-by-config choice here — it's the POC
/// stand-in for the court's Document Management System API, so there's no second backend for this one to switch
/// between.
///
/// <para>
/// <b>One container per queue entry</b> (<see cref="ContainerNameFor"/>): this means a future authorization
/// layer only ever has to reason about "does this staff session have access to this entry's container", not
/// filter individual blobs out of one shared listing — container-per-entry is what makes that boundary cheap to
/// enforce later, even though this implementation itself ships no per-container ACLs.
/// </para>
/// <para>
/// <b>Blob names are always server-generated GUIDs</b> (<see cref="NewBlobName"/>), never the client-supplied
/// filename: a client-controlled storage path on a public, unauthenticated upload endpoint is a path-traversal
/// and same-name-collision vector. The original filename is kept only as display metadata
/// (<c>DocumentMetadata.FileName</c>), never used to address storage.
/// </para>
/// </summary>
public sealed class DocumentBlobStore(BlobServiceClient blobServiceClient)
{
  // Explicit field (not a bare captured primary-constructor parameter) so call sites can use the
  // this.blobServiceClient prefix required by CLAUDE.md's C# style for instance members.
  private readonly BlobServiceClient blobServiceClient = blobServiceClient;

  /// <summary>
  /// The container name for an entry's documents. Entry ids are server-assigned GUIDs (lowercase hex digits and
  /// hyphens), which already satisfy Blob Storage's container-name character rules, so a fixed prefix is enough
  /// — no further sanitization is needed before this is safe to pass to <see cref="BlobServiceClient.GetBlobContainerClient"/>.
  /// </summary>
  public static string ContainerNameFor(string entryId) => $"docs-{entryId}";

  /// <summary>A fresh, randomized blob name — see type remarks for why the client's filename is never used here.</summary>
  public static string NewBlobName() => Guid.NewGuid().ToString();

  /// <summary>
  /// Uploads <paramref name="content"/> to <paramref name="entryId"/>'s container under <paramref name="blobName"/>,
  /// creating the container on first upload for that entry (containers aren't pre-provisioned — most entries
  /// never receive a document at all, so there's no reason to create one for every check-in).
  /// </summary>
  public async Task UploadAsync(
    string entryId, string blobName, string contentType, Stream content, CancellationToken ct = default)
  {
    BlobContainerClient container = this.blobServiceClient.GetBlobContainerClient(ContainerNameFor(entryId));
    await container.CreateIfNotExistsAsync(cancellationToken: ct);

    BlobClient blob = container.GetBlobClient(blobName);
    await blob.UploadAsync(
      content,
      new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = contentType } },
      ct);
  }

  /// <summary>
  /// Opens a readable stream for a previously uploaded blob, or null if it's missing. A miss should only happen
  /// after manual interference with the emulator's data — Blob Storage and the metadata store aren't written
  /// transactionally together (the upload endpoint writes the blob first, specifically so a metadata row can
  /// never reference content that doesn't exist), so this signature exists to let the caller distinguish that
  /// from "no such document at all" rather than assuming the pairing can never drift.
  /// </summary>
  public async Task<Stream?> OpenReadAsync(string entryId, string blobName, CancellationToken ct = default)
  {
    BlobClient blob = this.blobServiceClient.GetBlobContainerClient(ContainerNameFor(entryId)).GetBlobClient(blobName);

    // A single OpenReadAsync (catching the 404) rather than ExistsAsync-then-OpenReadAsync: one round-trip
    // instead of two, and it closes the window where a blob deleted between the two calls would turn the
    // intended graceful null into an unhandled 404 exception. A missing container surfaces as the same 404.
    try
    {
      return await blob.OpenReadAsync(cancellationToken: ct);
    }
    catch (RequestFailedException ex) when (ex.Status == (int)HttpStatusCode.NotFound)
    {
      return null;
    }
  }
}
