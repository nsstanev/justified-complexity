using Microsoft.EntityFrameworkCore;

namespace DuplicateCharge;

public sealed record PaymentRequest(string OrderId, decimal Amount);

public sealed record PaymentResponse(Guid PaymentId, string OrderId, decimal Amount, string ProviderChargeId);

// The API's own record of a payment.
public sealed class Payment
{
    public Guid Id { get; set; }
    public required string OrderId { get; set; }
    public decimal Amount { get; set; }
    public required string ProviderChargeId { get; set; }
    public string? IdempotencyKey { get; set; } // null on the naive endpoint
}

// One row per Idempotency-Key the API has seen.
public sealed class IdempotencyRecord
{
    public const string InProgress = "in_progress";
    public const string Completed = "completed";

    public required string Key { get; set; }
    public required string RequestHash { get; set; }
    public required string Status { get; set; }
    public DateTime LockedAt { get; set; }
    public int? ResponseStatus { get; set; }
    public string? ResponseBody { get; set; }
}

// The payment provider's side of the story. In real life this table lives in someone else's system.
public sealed class ProviderCharge
{
    public int Id { get; set; }
    public required string OrderId { get; set; }
    public decimal Amount { get; set; }
    public string? IdempotencyKey { get; set; }
}

public sealed class AppDb(DbContextOptions<AppDb> options) : DbContext(options)
{
    public const string PaymentsKeyIndex = "ix_payments_idempotency_key";
    public const string ProviderKeyIndex = "ix_provider_charges_idempotency_key";

    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<IdempotencyRecord> IdempotencyKeys => Set<IdempotencyRecord>();
    public DbSet<ProviderCharge> ProviderCharges => Set<ProviderCharge>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Payment>(e =>
        {
            e.ToTable("payments");
            // The final guard: even if two workers finish the same key, only one payment row can exist.
            e.HasIndex(p => p.IdempotencyKey).IsUnique().HasDatabaseName(PaymentsKeyIndex);
        });

        // Explicit names, because Idempotency.cs claims keys with a hand-written SQL statement.
        b.Entity<IdempotencyRecord>(e =>
        {
            e.ToTable("idempotency_keys");
            e.HasKey(r => r.Key);
            e.Property(r => r.Key).HasColumnName("key").HasMaxLength(255);
            e.Property(r => r.RequestHash).HasColumnName("request_hash");
            e.Property(r => r.Status).HasColumnName("status");
            e.Property(r => r.LockedAt).HasColumnName("locked_at");
            e.Property(r => r.ResponseStatus).HasColumnName("response_status");
            e.Property(r => r.ResponseBody).HasColumnName("response_body");
        });

        b.Entity<ProviderCharge>(e =>
        {
            e.ToTable("provider_charges");
            e.HasIndex(c => c.IdempotencyKey).IsUnique().HasDatabaseName(ProviderKeyIndex);
        });
    }
}
