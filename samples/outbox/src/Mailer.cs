using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace OutboxSample;

// The consumer: it emails the invoice to the customer for every InvoiceIssued it hears about.
// The email is only a row in the emails table; nothing is sent.
// The outbox delivers at least once, so the same message can arrive twice. The inbox makes the second one a no-op.
public sealed class InvoiceMailer(IDbContextFactory<AppDb> factory, ILogger<InvoiceMailer> log)
{
    public async Task HandleAsync(Guid messageId, string type, string payload, CancellationToken ct)
    {
        if (type != nameof(InvoiceIssued))
        {
            return;
        }

        var issued = JsonSerializer.Deserialize<InvoiceIssued>(payload, JsonSerializerOptions.Web)!;

        await using var db = await factory.CreateDbContextAsync(ct);

        // The inbox row and the email are saved in one transaction (a single SaveChanges):
        // either both exist or neither does.
        db.InboxMessages.Add(new InboxMessage { MessageId = messageId, ProcessedAt = DateTime.UtcNow });
        db.Emails.Add(new SentEmail { Id = Guid.CreateVersion7(), InvoiceId = issued.InvoiceId, Number = issued.Number });

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: AppDb.InboxKey
        })
        {
            log.LogInformation("Message {MessageId} was already handled; skipped", messageId);
        }
    }
}
