using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace SignalRQueueDemo.ApiService.Endpoints;

/// <summary>
/// Turns the framework-level "request body too large" failures into a clean <c>413 Payload Too Large</c>
/// <see cref="ProblemDetails"/> instead of the opaque 500 they otherwise produce. The upload endpoint's
/// <c>WithFormOptions</c> body limit is a buffering backstop: a grossly oversized body is cut off by the form
/// reader *before* the handler runs, so it can't be rejected by the handler's own tidy size check. When the
/// multipart reader trips the limit it throws <see cref="InvalidDataException"/> ("Multipart body length limit N
/// exceeded"), which minimal-API form binding then wraps in a <see cref="BadHttpRequestException"/> ("Failed to
/// read parameter ... from the request body as form") — so this unwraps one level to recognize it. A
/// server-level body-size limit instead throws a <see cref="BadHttpRequestException"/> already carrying a 413.
/// Either way it would otherwise surface as a 500. Mapping it here keeps even the abuse path returning a
/// sensible status. The endpoint's in-handler check still gives the more precise "exceeds the 10 MB upload
/// limit" message for a file only modestly over the cap; this handles the far larger bodies that never reach
/// the handler at all.
/// </summary>
public sealed class UploadLimitExceptionHandler : IExceptionHandler
{
  public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
  {
    // The three "too large" shapes: a bare multipart-limit InvalidDataException; that same exception wrapped by
    // form-parameter binding into a BadHttpRequestException; and a server body-size BadHttpRequestException that
    // already carries a 413. Anything else is left for the default handler so unrelated failures keep their own
    // (correct) status. If the response has already started there's nothing we can rewrite, so bail to the default.
    bool tooLarge = exception is InvalidDataException
      || exception is BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge }
      || (exception is BadHttpRequestException && exception.InnerException is InvalidDataException);
    if (!tooLarge || httpContext.Response.HasStarted)
    {
      return false;
    }

    httpContext.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
    await httpContext.Response.WriteAsJsonAsync(
      new ProblemDetails
      {
        Status = StatusCodes.Status413PayloadTooLarge,
        Title = "Upload too large",
        Detail = "The upload exceeded the allowed request size."
      },
      cancellationToken);

    return true;
  }
}
