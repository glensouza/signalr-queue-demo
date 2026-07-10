using Microsoft.AspNetCore.Mvc;
using SignalRQueueDemo.ApiService.Hubs;
using SignalRQueueDemo.ApiService.Persistence;
using SignalRQueueDemo.Contracts;

namespace SignalRQueueDemo.ApiService.Endpoints;

/// <summary>
/// Maps the queue endpoints: check-in, call-next, complete, current-state read (issue #2), and the reconnect
/// catch-up read (issue #3). Pulled out of Program.cs into its own extension method purely for readability —
/// there's no DI or lifetime reason it couldn't live inline.
/// </summary>
public static class QueueEndpoints
{
  /// <summary>
  /// Registers /checkin, /queue/call-next, /queue/{id}/complete, GET /queue, and GET /queue/since/{seq}.
  /// </summary>
  public static void MapQueueEndpoints(this WebApplication app)
  {
    app.MapPost("/checkin", HandleCheckInAsync);
    app.MapPost("/queue/call-next", HandleCallNextAsync);
    app.MapPost("/queue/{id}/complete", HandleCompleteAsync);
    app.MapGet("/queue", HandleGetQueueAsync);
    app.MapGet("/queue/since/{sequenceNumber:long}", HandleGetSinceAsync);
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
