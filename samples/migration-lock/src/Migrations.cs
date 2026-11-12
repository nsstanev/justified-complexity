using System.Diagnostics;
using Npgsql;

namespace MigrationLock;

public sealed record MigrationResult(string Name, int Attempts, long ElapsedMs);

// The schema changes the demo runs, as plain SQL. What differs between each pair is how the change takes its lock.
public sealed class Migrations(NpgsqlDataSource dataSource, ILogger<Migrations> log)
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(700);

    public static readonly string[] Names = ["add-column", "add-column-safe", "index", "index-concurrently", "check", "check-not-valid"];

    public async Task<MigrationResult> RunAsync(string name, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var attempts = name switch
        {
            // Waits for its lock for as long as it takes. Every query that arrives meanwhile waits behind it.
            "add-column" => await RunOnceAsync("ALTER TABLE invoices ADD COLUMN notes text", ct),

            // Gives up after 300 ms, lets the queue drain and tries again.
            "add-column-safe" => await RunWithLockTimeoutAsync("ALTER TABLE invoices ADD COLUMN reference text", ct),

            // Blocks every INSERT, UPDATE and DELETE on the table until the index is built. Reads continue.
            "index" => await RunOnceAsync("CREATE INDEX ix_invoices_customer ON invoices (customer)", ct),

            // Builds the index without blocking writes. It cannot run inside a transaction.
            "index-concurrently" => await BuildIndexConcurrentlyAsync(ct),

            // Checks every row while holding the lock that blocks reads and writes.
            "check" => await RunOnceAsync("ALTER TABLE invoices ADD CONSTRAINT ck_invoices_number CHECK (number ~ '^INV-[0-9]+$')", ct),

            // Two steps. NOT VALID applies the rule to new rows at once and skips the scan.
            // VALIDATE scans the old rows under a lock that lets reads and writes through.
            "check-not-valid" => await RunWithLockTimeoutAsync("ALTER TABLE invoices ADD CONSTRAINT ck_invoices_number_safe CHECK (number ~ '^INV-[0-9]+$') NOT VALID", ct)
                + await RunOnceAsync("ALTER TABLE invoices VALIDATE CONSTRAINT ck_invoices_number_safe", ct) - 1,

            _ => throw new ArgumentException($"Unknown migration '{name}'."),
        };

        return new MigrationResult(name, attempts, watch.ElapsedMilliseconds);
    }

    private async Task<int> RunOnceAsync(string sql, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand(sql);
        command.CommandTimeout = 120;
        await command.ExecuteNonQueryAsync(ct);
        return 1;
    }

    // A concurrent build that fails or is cancelled leaves an index behind that is marked invalid:
    // it costs every write and is used by no query. A build that returns without an error is valid,
    // so the places to look are after a failure and before a rerun. Never hide it with IF NOT EXISTS.
    private async Task<int> BuildIndexConcurrentlyAsync(CancellationToken ct)
    {
        const string index = "ix_invoices_customer_concurrent";

        // Left behind by an earlier run that failed.
        await DropIfInvalidAsync(index, ct);

        try
        {
            await RunOnceAsync($"CREATE INDEX CONCURRENTLY {index} ON invoices (customer)", ct);
        }
        catch
        {
            // Not the request's token: the cleanup has to run when the build was cancelled, too.
            await DropIfInvalidAsync(index, CancellationToken.None);
            throw;
        }

        return 1;
    }

    private async Task DropIfInvalidAsync(string index, CancellationToken ct)
    {
        await using var check = dataSource.CreateCommand("SELECT indisvalid FROM pg_index WHERE indexrelid = to_regclass(@index)");
        check.Parameters.AddWithValue("index", index);
        if (await check.ExecuteScalarAsync(ct) is false)
        {
            log.LogWarning("Index {Index} is invalid; dropping it", index);
            await RunOnceAsync($"DROP INDEX CONCURRENTLY {index}", ct);
        }
    }

    private async Task<int> RunWithLockTimeoutAsync(string sql, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            await using var connection = await dataSource.OpenConnectionAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(ct);
            try
            {
                // SET LOCAL lasts until the end of this transaction, so the pooled connection is not changed.
                await using (var timeout = new NpgsqlCommand("SET LOCAL lock_timeout = '300ms'", connection, transaction))
                {
                    await timeout.ExecuteNonQueryAsync(ct);
                }

                await using (var command = new NpgsqlCommand(sql, connection, transaction))
                {
                    await command.ExecuteNonQueryAsync(ct);
                }

                await transaction.CommitAsync(ct);
                return attempt;
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.LockNotAvailable && attempt < 40)
            {
                // The lock was busy. Rolling back takes the statement out of the lock queue,
                // and the requests that were waiting behind it go ahead.
                await transaction.RollbackAsync(ct);
                log.LogInformation("Attempt {Attempt}: lock not available, retrying in {Delay} ms", attempt, RetryDelay.TotalMilliseconds);
                await Task.Delay(RetryDelay, ct);
            }
        }
    }

    // A long-running read: a transaction that touches the table and then stays open.
    // It holds the weakest table lock there is (ACCESS SHARE), and that is enough to make ALTER TABLE wait.
    public async Task RunReportAsync(int seconds)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var read = new NpgsqlCommand("SELECT count(*) FROM invoices WHERE id <= 1000", connection, transaction))
        {
            await read.ExecuteScalarAsync();
        }

        await using (var sleep = new NpgsqlCommand("SELECT pg_sleep(@seconds)", connection, transaction))
        {
            sleep.Parameters.AddWithValue("seconds", (double)seconds);
            await sleep.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }
}
