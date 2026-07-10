using Microsoft.EntityFrameworkCore;
using SignalRQueueDemo.Contracts;

namespace SignalRQueueDemo.ApiService.Persistence.Sqlite;

/// <summary>
/// Seeds a couple of obviously-fake entries on first run so the queue isn't empty the first time someone
/// opens a frontend against a fresh `.db` file. Names/tickets match the synthetic examples named in
/// CLAUDE.md ("Ticket A-042", "Jane Test", "Sam Sample") — a hard court constraint against any real names.
/// </summary>
public static class QueueSeedData
{
  public static async Task SeedIfEmptyAsync(QueueDbContext dbContext, CancellationToken ct = default)
  {
    if (await dbContext.Entries.AnyAsync(ct))
    {
      return;
    }

    DateTimeOffset now = DateTimeOffset.UtcNow;

    QueueEntryEntity[] seedEntries =
    [
      new QueueEntryEntity
      {
        Id = Guid.NewGuid().ToString(),
        DisplayName = "Jane Test",
        TicketNumber = "A-042",
        CheckedInAt = now.AddMinutes(-10),
        Status = QueueStatus.Waiting
      },
      new QueueEntryEntity
      {
        Id = Guid.NewGuid().ToString(),
        DisplayName = "Sam Sample",
        TicketNumber = "A-043",
        CheckedInAt = now.AddMinutes(-5),
        Status = QueueStatus.Waiting
      }
    ];

    dbContext.Entries.AddRange(seedEntries);
    foreach (QueueEntryEntity entry in seedEntries)
    {
      dbContext.ChangeEvents.Add(QueueChangeEventEntity.FromEntry(entry));
    }

    await dbContext.SaveChangesAsync(ct);
  }
}
