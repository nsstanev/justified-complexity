using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace OutboxSample;

public sealed record IssueInvoiceRequest(string Number, decimal Total);

public sealed record InvoiceResponse(Guid InvoiceId, string Number, decimal Total);

// The event other systems hear about. Its JSON is the contract with the consumers.
public sealed record InvoiceIssued(Guid InvoiceId, string Number, decimal Total);

public static class InvoiceRules
{
    public static Dictionary<string, string[]> Validate(IssueInvoiceRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(request.Number) || request.Number.Length > 64)
        {
            errors["number"] = ["'number' is required, at most 64 characters."];
        }

        if (request.Total <= 0 || request.Total > 1_000_000)
        {
            errors["total"] = ["'total' must be greater than zero."];
        }

        return errors;
    }
}

public sealed class Invoice
{
    public Guid Id { get; set; }
    public required string Number { get; set; }
    public decimal Total { get; set; }
    public DateTime IssuedAt { get; set; }

    public static Invoice From(IssueInvoiceRequest request) =>
        new() { Id = Guid.CreateVersion7(), Number = request.Number, Total = request.Total, IssuedAt = DateTime.UtcNow };

    public InvoiceIssued ToEvent() => new(Id, Number, Total);

    public InvoiceResponse ToResponse() => new(Id, Number, Total);
}

// A message that still has to reach the broker. It is written in the same transaction as the invoice.
public sealed class OutboxMessage
{
    public Guid Id { get; set; } // also the message id the consumer deduplicates on
    public required string Type { get; set; }
    public required string Payload { get; set; }
    public DateTime OccurredAt { get; set; }
    public DateTime? ProcessedAt { get; set; } // null until the broker has it
    public int Attempts { get; set; }
    public string? LastError { get; set; }

    public static OutboxMessage From<T>(T @event) => new()
    {
        Id = Guid.CreateVersion7(), // time-ordered, so ORDER BY id is roughly the order of writing
        Type = typeof(T).Name,
        Payload = JsonSerializer.Serialize(@event, JsonSerializerOptions.Web),
        OccurredAt = DateTime.UtcNow,
    };
}

// The broker's side of the story: every message it accepted. In real life this lives in the broker.
public sealed class BrokerMessage
{
    public int Id { get; set; }
    public Guid MessageId { get; set; }
    public required string Type { get; set; }
    public required string Payload { get; set; }
}

// The consumer's inbox: one row per message it has handled. In real life this is the consumer's own database.
public sealed class InboxMessage
{
    public Guid MessageId { get; set; }
    public DateTime ProcessedAt { get; set; }
}

// What the consumer does with InvoiceIssued: one email to the customer per invoice.
public sealed class SentEmail
{
    public Guid Id { get; set; }
    public Guid InvoiceId { get; set; }
    public required string Number { get; set; }
}

public sealed class AppDb(DbContextOptions<AppDb> options) : DbContext(options)
{
    public const string InboxKey = "pk_inbox_messages";

    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<BrokerMessage> BrokerMessages => Set<BrokerMessage>();
    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();
    public DbSet<SentEmail> Emails => Set<SentEmail>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Invoice>().ToTable("invoices");

        // Explicit names, because Outbox.cs reads this table with a hand-written SQL statement.
        b.Entity<OutboxMessage>(e =>
        {
            e.ToTable("outbox_messages");
            e.Property(m => m.Id).HasColumnName("id");
            e.Property(m => m.Type).HasColumnName("type");
            e.Property(m => m.Payload).HasColumnName("payload");
            e.Property(m => m.OccurredAt).HasColumnName("occurred_at");
            e.Property(m => m.ProcessedAt).HasColumnName("processed_at");
            e.Property(m => m.Attempts).HasColumnName("attempts");
            e.Property(m => m.LastError).HasColumnName("last_error");
            // The dispatcher only ever looks for unsent rows, so index only those. The index stays small
            // however many sent rows the table keeps.
            e.HasIndex(m => m.Id).HasDatabaseName("ix_outbox_messages_unsent").HasFilter("processed_at IS NULL");
        });

        b.Entity<BrokerMessage>().ToTable("broker_messages");

        b.Entity<InboxMessage>(e =>
        {
            e.ToTable("inbox_messages");
            // The primary key is the deduplication: a second delivery of the same message cannot insert.
            e.HasKey(m => m.MessageId).HasName(InboxKey);
        });

        b.Entity<SentEmail>().ToTable("emails");
    }
}
