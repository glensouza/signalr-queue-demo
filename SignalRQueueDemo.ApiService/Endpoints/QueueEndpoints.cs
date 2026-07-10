using Microsoft.AspNetCore.Mvc;
using SignalRQueueDemo.ApiService.Persistence;
using SignalRQueueDemo.Contracts;

namespace SignalRQueueDemo.ApiService.Endpoints;

/// <summary>
/// Maps the four queue endpoints from issue #2: check-in, call-next, complete, and the current-state read.
/// Pulled out of Program.cs into its own extension method purely for readability — there's no DI or lifetime
/// reason it couldn't live inline.
/// </summary>
public static class QueueEndpoints
{
  /// <summary>Registers /checkin, /queue/call-next, /queue/{id}/complete, and GET /queue on the app.</summary>
  public static void MapQueueEndpoints(this WebApplication app)
  {
    app.MapPost("/checkin", HandleCheckInAsync);
    app.MapPost("/queue/call-next", HandleCallNextAsync);
    app.MapPost("/queue/{id}/complete", HandleCompleteAsync);
    app.MapGet("/queue", HandleGetQueueAsync);
  }

  private static async Task<IResult> HandleCheckInAsync(
    CheckInRequest request,
    IQueueRepository repository,
    CancellationToken ct)
  {
    if (string.IsNullOrWhiteSpace(request.DisplayName) || string.IsNullOrWhiteSpace(request.TicketNumber))
    {
      return Results.ValidationProblem(new Dictionary<string, string[]>
      {
        [nameof(CheckInRequest.DisplayName)] = ["Display name and ticket number are both required."]
      });
    }

    CheckInResponse response = await repository.CheckInAsync(request, ct);
    return Results.Ok(response);
  }

  private static async Task<IResult> HandleCallNextAsync(IQueueRepository repository, CancellationToken ct)
  {
    QueueOperationResult result = await repository.CallNextAsync(ct);

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
    CancellationToken ct)
  {
    QueueOperationResult result = await repository.CompleteAsync(id, ct);

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
}
