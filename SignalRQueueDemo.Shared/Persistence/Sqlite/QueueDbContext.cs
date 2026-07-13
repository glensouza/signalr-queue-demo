using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using SignalRQueueDemo.Contracts;

namespace SignalRQueueDemo.Shared.Persistence.Sqlite;

/// <summary>
/// EF Core context for the SQLite-backed queue store. Uses <c>EnsureCreatedAsync</c> at startup instead of
/// migrations (see Program.cs) — this is a POC with no schema history to preserve, and EnsureCreated is a
/// single call instead of a migrations project + design-time factory. Note the well-known EnsureCreated
/// tradeoff: it stamps the schema once and does nothing on later model changes, so any future column
/// addition needs either a manual `DROP` of the dev `.db` file or a switch to real migrations at that point.
/// </summary>
public sealed class QueueDbContext(DbContextOptions<QueueDbContext> options) : DbContext(options)
{
  /// <summary>Current-state rows — one per queue entry, mutated in place as it moves Waiting → Serving → Completed.</summary>
  public DbSet<QueueEntryEntity> Entries => this.Set<QueueEntryEntity>();

  /// <summary>Append-only change log — one immutable row per state change, keyed by the monotonic sequence number.</summary>
  public DbSet<QueueChangeEventEntity> ChangeEvents => this.Set<QueueChangeEventEntity>();

  /// <summary>Uploaded-document metadata — see <see cref="DocumentEntity"/> for why this is a separate table from <see cref="Entries"/>.</summary>
  public DbSet<DocumentEntity> Documents => this.Set<DocumentEntity>();

  protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
  {
    // Applied as conventions (every property of these types, across both tables) rather than per-column so a
    // future DateTimeOffset/enum column can't silently reintroduce the bugs these guard against.
    //
    // SQLite's EF provider can't translate ORDER BY over a raw DateTimeOffset (no native offset-aware type),
    // which crashes call-next / the snapshot at runtime. DateTimeOffsetToBinaryConverter stores the instant
    // as a single long that sorts by UTC instant *and* preserves the original offset, so ordering stays
    // server-side and both tables hold the timestamp in one identical physical format.
    configurationBuilder.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToBinaryConverter>();

    // Enums stored as text (not their numeric value) so the .db file is readable in any SQLite browser during
    // development/demo — no query-performance concern at this scale (dozens of rows).
    configurationBuilder.Properties<QueueStatus>().HaveConversion<string>();
  }

  protected override void OnModelCreating(ModelBuilder modelBuilder)
  {
    modelBuilder.Entity<QueueEntryEntity>(entity =>
    {
      entity.ToTable("QueueEntries");
      entity.HasKey(e => e.Id);
    });

    modelBuilder.Entity<QueueChangeEventEntity>(entity =>
    {
      entity.ToTable("QueueChangeEvents");
      entity.HasKey(e => e.SequenceNumber);
      entity.Property(e => e.SequenceNumber).ValueGeneratedOnAdd();
    });

    modelBuilder.Entity<DocumentEntity>(entity =>
    {
      entity.ToTable("Documents");
      entity.HasKey(e => e.Id);
      // Every read (list, single-document lookup) filters by EntryId first, so it's indexed rather than relying
      // on a full-table scan — cheap here at demo scale, but it's the right habit for a reference implementation.
      entity.HasIndex(e => e.EntryId);
    });
  }
}
