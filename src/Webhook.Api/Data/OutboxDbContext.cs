using Microsoft.EntityFrameworkCore;
using Webhook.Api.Entities;

namespace Webhook.Api.Data;

public class OutboxDbContext(DbContextOptions<OutboxDbContext> options) : DbContext(options)
{
    public virtual DbSet<Outbox> Outbox { get; set; } = null!;
    public virtual DbSet<DeadLetter> OutboxDeadLetter { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Outbox>(b =>
        {
            b.ToTable(nameof(Outbox));
            b.HasKey(x => x.Id);

            b.Property(x => x.Payload).HasColumnType("bytea").IsRequired();
            b.Property(x => x.ContentType).HasColumnType("text");
            b.Property(x => x.HeadersJson).HasColumnType("text");
            b.Property(x => x.CreatedAtUtc).HasColumnType("timestamptz").IsRequired();
            b.Property(x => x.Attempts).HasColumnType("integer").IsRequired();
            b.Property(x => x.NextAttemptUtc).HasColumnType("timestamptz").IsRequired();
            b.Property(x => x.LockedUntil).HasColumnType("timestamptz");
            b.Property(x => x.LockOwner).HasColumnType("text");
        });

        modelBuilder.Entity<DeadLetter>(b =>
        {
            b.ToTable(nameof(DeadLetter));
            b.HasKey(x => x.Id);

            b.Property(x => x.OriginalId).HasColumnType("bigint");
            b.Property(x => x.Payload).HasColumnType("bytea");
            b.Property(x => x.ContentType).HasColumnType("text");
            b.Property(x => x.HeadersJson).HasColumnType("text");
            b.Property(x => x.CreatedAtUtc).HasColumnType("timestamptz");
            b.Property(x => x.FailedAtUtc).HasColumnType("timestamptz");
            b.Property(x => x.Attempts).HasColumnType("integer");
            b.Property(x => x.Error).HasColumnType("text");
        });
    }
}