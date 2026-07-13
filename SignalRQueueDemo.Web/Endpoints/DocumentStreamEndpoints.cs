using SignalRQueueDemo.Shared.Persistence;
using SignalRQueueDemo.Shared.Persistence.Blob;
using SignalRQueueDemo.Web.Services;

namespace SignalRQueueDemo.Web.Endpoints;

/// <summary>
/// The one local minimal-API endpoint this Blazor Server app hosts: streaming an uploaded document's bytes back
/// to the browser for the staff <c>DocumentViewer</c>. Exists only because HTML has no way to push bytes through
/// a SignalR/Razor render into an <c>&lt;iframe&gt;</c>/<c>&lt;img&gt;</c> — those need a real URL to fetch. This
/// is NOT a call to <c>SignalRQueueDemo.ApiService</c>'s own document-streaming REST endpoint: it calls
/// <see cref="IDocumentRepository"/>/<see cref="DocumentBlobStore"/> directly, same as that endpoint does, so
/// "Blazor never calls the API over REST" (docs/decisions.md) stays true even for this one byte-streaming
/// necessity. Gated by <see cref="DocumentAccessTokenService"/> instead of the <c>X-Staff-Key</c> header the REST
/// endpoint uses — see that service's remarks for why a header-based gate doesn't work for a plain browser GET.
/// </summary>
public static class DocumentStreamEndpoints
{
  public static void MapDocumentStreamEndpoints(this WebApplication app)
  {
    app.MapGet("/staff/documents/{entryId}/{docId}", HandleGetDocumentAsync);
  }

  private static async Task<IResult> HandleGetDocumentAsync(
    string entryId,
    string docId,
    string? token,
    DocumentAccessTokenService tokenService,
    IDocumentRepository documentRepository,
    DocumentBlobStore blobStore,
    CancellationToken ct)
  {
    if (!tokenService.Validate(entryId, docId, token))
    {
      return Results.Unauthorized();
    }

    DocumentRecord? record = await documentRepository.GetDocumentAsync(entryId, docId, ct);
    if (record is null)
    {
      return Results.NotFound();
    }

    Stream? blobStream = await blobStore.OpenReadAsync(entryId, record.BlobName, ct);
    if (blobStream is null)
    {
      // See DocumentBlobStore.OpenReadAsync remarks: metadata exists but the blob doesn't — should only happen
      // after manual interference with the emulator's data.
      return Results.Problem(
        title: "Document content missing",
        detail: $"Document '{docId}' has metadata but its content could not be found in Blob Storage.",
        statusCode: StatusCodes.Status500InternalServerError);
    }

    return Results.File(blobStream, record.Metadata.ContentType, record.Metadata.FileName);
  }
}
