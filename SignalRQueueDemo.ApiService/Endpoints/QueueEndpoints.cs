using Microsoft.AspNetCore.Mvc;
using SignalRQueueDemo.ApiService.Hubs;
using SignalRQueueDemo.ApiService.Persistence;
using SignalRQueueDemo.Contracts;

namespace SignalRQueueDemo.ApiService.Endpoints;

/// <summary>
/// Maps the queue endpoints: the check-in token issuer, check-in, call-next, complete, current-state read, and
/// the reconnect catch-up read. Pulled out of Program.cs into its own extension method purely for readability —
/// there's no DI or lifetime reason it couldn't live inline.
/// </summary>
public static class QueueEndpoints
{
  /// <summary>
  /// Registers GET /checkin/token, POST /checkin, POST /queue/call-next, POST /queue/{id}/complete,
  /// GET /queue, and GET /queue/since/{seq}.
  ///
  /// <para>
  /// Every route gets the <see cref="CorsPolicies.KnownFrontends"/> policy — the staff routes need it just as
  /// much as the public ones, since the (cross-origin) staff Angular app is a known frontend too; CORS is not
  /// the trust boundary here (that's <see cref="StaffAuthFilter"/> on call-next/complete), it only keeps the
  /// legitimate frontends from being refused by the browser before that check runs. The check-in token filter
  /// (<see cref="CheckInTokenFilter"/>) gates the check-in POST; the token issuer and the two read endpoints
  /// stay ungated by it, per the anti-forgery-style pattern's usual scope of "protect writes, not reads".
  /// </para>
  /// </summary>
  public static void MapQueueEndpoints(this WebApplication app)
  {
    app.MapGet("/checkin/token", HandleIssueCheckInToken)
      .RequireCors(CorsPolicies.KnownFrontends);
    app.MapPost("/checkin", HandleCheckInAsync)
      .RequireCors(CorsPolicies.KnownFrontends)
      .AddEndpointFilter<CheckInTokenFilter>();
    app.MapPost("/queue/call-next", HandleCallNextAsync)
      .RequireCors(CorsPolicies.KnownFrontends)
      .AddEndpointFilter<StaffAuthFilter>();
    app.MapPost("/queue/{id}/complete", HandleCompleteAsync)
      .RequireCors(CorsPolicies.KnownFrontends)
      .AddEndpointFilter<StaffAuthFilter>();
    app.MapGet("/queue", HandleGetQueueAsync)
      .RequireCors(CorsPolicies.KnownFrontends);
    app.MapGet("/queue/since/{sequenceNumber:long}", HandleGetSinceAsync)
      .RequireCors(CorsPolicies.KnownFrontends);
  }

  /// <summary>
  /// Issues a fresh short-lived token a kiosk client must echo back on <c>POST /checkin</c> — see
  /// <see cref="CheckInTokenService"/> for what this does and doesn't protect against. Synchronous (no
  /// <c>Async</c> suffix): issuing a token is pure computation, no I/O.
  /// </summary>
  private static IResult HandleIssueCheckInToken(CheckInTokenService tokenService, HttpResponse response)
  {
    // no-store so a security token is never retained by the browser's bfcache or a shared proxy and later
    // replayed or leaked — a token-issuing GET must not be cacheable.
    response.Headers.CacheControl = "no-store";
    return Results.Ok(new CheckInTokenResponse { Token = tokenService.Issue() });
  }

  private static async Task<IResult> HandleCheckInAsync(
    CheckInRequest request,
    IQueueRepository repository,
    QueueBroadcaster broadcaster,
    CancellationToken ct)
  {
    if (string.IsNullOrWhiteSpace(request.DisplayName) || string.IsNullOrWhiteSpace(request.TicketNumber))
    {
      return Results.ValidationProblem(new Dictionary<string, string[]>
      {
        [nameof(CheckInRequest.DisplayName)] = ["Display name and ticket number are both required."]
      });
    }

    CheckInResult result = await repository.CheckInAsync(request, ct);

    // Persist first, then broadcast. The broadcaster never throws (a failed push can't fail this committed
    // check-in — see QueueBroadcaster), and CheckInAsync built result.Update from the same committed state, so
    // check-in broadcasts exactly like call-next/complete instead of re-assembling a payload here.
    await broadcaster.BroadcastAsync(result.Update);

    return Results.Ok(result.Response);
  }

  private static async Task<IResult> HandleCallNextAsync(
    IQueueRepository repository,
    QueueBroadcaster broadcaster,
    CancellationToken ct)
  {
    QueueOperationResult result = await repository.CallNextAsync(ct);

    if (result.Outcome == QueueOperationOutcome.Success)
    {
      // Success always carries a non-null Update (QueueOperationResult.Success); Failure never reports Success.
      await broadcaster.BroadcastAsync(result.Update!);
    }

    return result.Outcome switch
    {
      QueueOperationOutcome.Success => Results.Ok(result.Update),
      QueueOperationOutcome.NoWaitingEntries => Results.Conflict(new ProblemDetails
      {
        Title = "No entries waiting",
        Detail = "There are no entries with status Waiting to call next."
      }),
      _ => Results.Problem("Unexpected outcome calling the next queue entry.")
    };
  }

  private static async Task<IResult> HandleCompleteAsync(
    string id,
    IQueueRepository repository,
    QueueBroadcaster broadcaster,
    CancellationToken ct)
  {
    QueueOperationResult result = await repository.CompleteAsync(id, ct);

    if (result.Outcome == QueueOperationOutcome.Success)
    {
      // Success always carries a non-null Update (QueueOperationResult.Success); Failure never reports Success.
      await broadcaster.BroadcastAsync(result.Update!);
    }

    return result.Outcome switch
    {
      QueueOperationOutcome.Success => Results.Ok(result.Update),
      QueueOperationOutcome.EntryNotFound => Results.NotFound(new ProblemDetails
      {
        Title = "Entry not found",
        Detail = $"No queue entry with id '{id}' exists."
      }),
      QueueOperationOutcome.InvalidState => Results.Conflict(new ProblemDetails
      {
        Title = "Entry is not being served",
        Detail = $"Queue entry '{id}' must be in the Serving status to be completed."
      }),
      _ => Results.Problem("Unexpected outcome completing the queue entry.")
    };
  }

  private static async Task<IResult> HandleGetQueueAsync(IQueueRepository repository, CancellationToken ct) =>
    Results.Ok(await repository.GetStateAsync(ct));

  /// <summary>
  /// The REST half of the reconnect/catch-up protocol — see QueueHub for the SignalR half. sequenceNumber is
  /// route-constrained to :long (not validated here) because an unrecognized value (negative, or ahead of
  /// everything the server has issued) is a normal, expected input, not an error: the repository already
  /// treats it as "start over from a full snapshot" rather than throwing. See
  /// SqliteQueueRepository.GetChangesSinceAsync for the exact cutoff.
  /// </summary>
  private static async Task<IResult> HandleGetSinceAsync(
    long sequenceNumber,
    IQueueRepository repository,
    CancellationToken ct) =>
    Results.Ok(await repository.GetChangesSinceAsync(sequenceNumber, ct));
}
