using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DuplicateCharge;

// Stands in for the payment provider. It commits each charge on its own connection,
// so a charge survives even when the API request that asked for it fails afterwards.
public sealed class FakePaymentProvider(IDbContextFactory<AppDb> factory)
{
    public async Task<string> ChargeAsync(string orderId, decimal amount, string? idempotencyKey, int delayMs, CancellationToken ct)
    {
        await Task.Delay(delayMs, ct); // the network round trip and the card processing

        await using var db = await factory.CreateDbContextAsync(ct);
        var charge = new ProviderCharge { OrderId = orderId, Amount = amount, IdempotencyKey = idempotencyKey };
        db.ProviderCharges.Add(charge);
        try
        {
            await db.SaveChangesAsync(ct);
            return $"ch_{charge.Id}";
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: AppDb.ProviderKeyIndex
        })
        {
            // The provider has seen this key before: return the original charge, don't charge again.
            db.ChangeTracker.Clear();
            var original = await db.ProviderCharges.SingleAsync(c => c.IdempotencyKey == idempotencyKey, ct);
            return $"ch_{original.Id}";
        }
    }
}
