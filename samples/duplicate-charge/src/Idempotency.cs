using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace DuplicateCharge;

public enum ClaimOutcome
{
    Claimed,    // this request owns the key: do the work
    Replay,     // the work is done: answer with the stored response
    InProgress, // another request owns the key and is still working
    Mismatch,   // the key was first used with a different request body
}

public sealed record Claim(ClaimOutcome Outcome, IdempotencyRecord? Record = null);

public static class IdempotencyStore
{
    // Same body, same hash. A key that arrives with a different hash is a client bug, not a retry.
    public static string Hash(PaymentRequest request) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request)));

    public static async Task<Claim> ClaimAsync(AppDb db, string key, string hash, TimeSpan lockTimeout, CancellationToken ct)
    {
        var staleBefore = DateTime.UtcNow - lockTimeout;

        // One statement, so the primary key decides which request owns the key.
        // A new key is inserted. An existing key is taken over only when its owner
        // stopped without finishing (same request, still in progress, lock expired).
        var claimed = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO idempotency_keys (key, request_hash, status, locked_at)
            VALUES ({key}, {hash}, 'in_progress', now())
            ON CONFLICT (key) DO UPDATE SET locked_at = now()
            WHERE idempotency_keys.status = 'in_progress'
              AND idempotency_keys.request_hash = {hash}
              AND idempotency_keys.locked_at < {staleBefore}
            """, ct);

        if (claimed == 1)
        {
            return new Claim(ClaimOutcome.Claimed);
        }

        var record = await db.IdempotencyKeys.AsNoTracking().SingleAsync(r => r.Key == key, ct);
        if (record.RequestHash != hash)
        {
            return new Claim(ClaimOutcome.Mismatch);
        }

        return record.Status == IdempotencyRecord.Completed
            ? new Claim(ClaimOutcome.Replay, record)
            : new Claim(ClaimOutcome.InProgress);
    }
}
