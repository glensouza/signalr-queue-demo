using Microsoft.AspNetCore.Mvc;
using SignalRQueueDemo.ApiService.Hubs;
using SignalRQueueDemo.Contracts;
using SignalRQueueDemo.Shared.Documents;
using SignalRQueueDemo.Shared.Persistence;

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
  /// GET /queue, GET /queue/since/{seq}, and GET /staff/verify.
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
    // Cancel is a public (kiosk-initiated) write, so it carries the same CheckInTokenFilter as POST /checkin rather
    // than the StaffAuthFilter the staff-only complete/call-next use — a visitor tapping "Stop tracking" on the kiosk
    // isn't staff, but this must not be an unauthenticated write either (that would let any caller cancel and delete
    // the documents of an arbitrary entry by id). It follows the "protect writes, not reads" anti-forgery pattern the
    // rest of the public path uses; see the 2026-07-12 cancel decision in docs/decisions.md.
    app.MapPost("/queue/{id}/cancel", HandleCancelAsync)
      .RequireCors(CorsPolicies.KnownFrontends)
      .AddEndpointFilter<CheckInTokenFilter>();
    // A read-only "is this staff key valid?" probe, gated by the same StaffAuthFilter as the real staff actions.
    // It exists so the internal-queue sign-in screen can reject a wrong key up front instead of silently accepting
    // any non-empty string and only surfacing the 401 on the first call-next/complete/document view. The filter
    // does all the work — a valid key falls through to a 204, a missing/wrong one is turned away with the same 401
    // ProblemDetails as everywhere else, so the client has exactly one code path to interpret. Deliberately carries
    // no body and mutates nothing: it models "validate credentials", not a session grant (this mock auth has no
    // server-side session — see StaffAuthFilter).
    app.MapGet("/staff/verify", () => Results.NoContent())
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
    QueueCompletionService completionService,
    QueueBroadcaster broadcaster,
    CancellationToken ct)
  {
    // Completion + document cleanup both live in QueueCompletionService (SignalRQueueDemo.Shared) so Blazor's
    // staff console — which calls it directly, bypassing this endpoint — gets the same document cleanup on
    // complete. Broadcasting stays here: it's the one part of this flow that differs per host (this endpoint
    // calls QueueBroadcaster directly; Blazor calls QueueRealtimeService.PublishAsync, a hub round-trip).
    QueueCompletionResult result = await completionService.CompleteAsync(id, ct);

    if (result.OperationResult.Outcome == QueueOperationOutcome.Success)
    {
      // Success always carries a non-null Update (QueueOperationResult.Success); Failure never reports Success.
      await broadcaster.BroadcastAsync(result.OperationResult.Update!);

      if (result.DocumentsClearedUpdate is not null)
      {
        await broadcaster.BroadcastAsync(result.DocumentsClearedUpdate);
      }
    }

    return result.OperationResult.Outcome switch
    {
      QueueOperationOutcome.Success => Results.Ok(result.OperationResult.Update),
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

  private static async Task<IResult> HandleCancelAsync(
    string id,
    QueueCancellationService cancellationService,
    QueueBroadcaster broadcaster,
    CancellationToken ct)
  {
    // Same split as HandleCompleteAsync: cancellation + document cleanup live in QueueCancellationService
    // (SignalRQueueDemo.Shared) so a Blazor-originated cancel (the kiosk's "Stop tracking" button) gets the same
    // document cleanup as this REST endpoint. Broadcasting stays here — the one part that differs per host.
    QueueCancellationResult result = await cancellationService.CancelAsync(id, ct);

    if (result.OperationResult.Outcome == QueueOperationOutcome.Success)
    {
      await broadcaster.BroadcastAsync(result.OperationResult.Update!);

      if (result.DocumentsClearedUpdate is not null)
      {
        await broadcaster.BroadcastAsync(result.DocumentsClearedUpdate);
      }
    }

    return result.OperationResult.Outcome switch
    {
      QueueOperationOutcome.Success => Results.Ok(result.OperationResult.Update),
      QueueOperationOutcome.EntryNotFound => Results.NotFound(new ProblemDetails
      {
        Title = "Entry not found",
        Detail = $"No queue entry with id '{id}' exists."
      }),
      QueueOperationOutcome.InvalidState => Results.Conflict(new ProblemDetails
      {
        Title = "Entry cannot be cancelled",
        Detail = $"Queue entry '{id}' must not be completed or cancelled already."
      }),
      _ => Results.Problem("Unexpected outcome cancelling the queue entry.")
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
