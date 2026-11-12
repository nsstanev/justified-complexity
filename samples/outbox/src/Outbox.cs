using Microsoft.EntityFrameworkCore;

namespace OutboxSample;

// Moves outbox rows to the broker. Several copies can run at once, in one process or in many:
// FOR UPDATE SKIP LOCKED gives each of them different rows.
public sealed class OutboxDispatcher(
    IDbContextFactory<AppDb> factory,
    FakeBroker broker,
    Faults faults,
    IConfiguration configuration,
    ILogger<OutboxDispatcher> log) : BackgroundService
{
    private const int BatchSize = 20;

    private readonly int _workers = configuration.GetValue("Outbox:Workers", 2);
    private readonly TimeSpan _poll = TimeSpan.FromMilliseconds(configuration.GetValue("Outbox:PollMilliseconds", 500));

    protected override Task ExecuteAsync(CancellationToken ct) =>
        Task.WhenAll(Enumerable.Range(1, _workers).Select(worker => RunAsync(worker, ct)));

    private async Task RunAsync(int worker, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_poll);
        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                // Keep going while there is a full batch, then wait for the next tick.
                while (await DispatchBatchAsync(ct) == BatchSize)
                {
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning("Outbox worker {Worker} failed: {Message}", worker, ex.Message);
            }
        }
    }

    private async Task<int> DispatchBatchAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        // Lock a batch of unsent rows. Rows another worker holds are skipped, not waited for.
        var batch = await db.OutboxMessages.FromSql($"""
            SELECT * FROM outbox_messages
            WHERE processed_at IS NULL
            ORDER BY id
            LIMIT {BatchSize}
            FOR UPDATE SKIP LOCKED
            """).ToListAsync(ct);

        var sent = 0;
        foreach (var message in batch)
        {
            try
            {
                await broker.PublishAsync(message.Id, message.Type, message.Payload, ct);
            }
            catch (BrokerUnavailableException ex)
            {
                // Leave the row unsent and stop: the rows after it would fail too. The next tick tries again.
                message.Attempts++;
                message.LastError = ex.Message;
                break;
            }

            if (faults.TakeFailAfterPublish())
            {
                // The transaction rolls back, the row stays unsent, and the next tick publishes it again.
                throw new InvalidOperationException("Simulated failure: the message is published, the row is not marked as sent.");
            }

            message.ProcessedAt = DateTime.UtcNow;
            sent++;
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return sent;
    }
}
