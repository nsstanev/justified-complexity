using Microsoft.EntityFrameworkCore;

namespace OutboxSample;

public sealed class BrokerUnavailableException() : Exception("The broker is not reachable.");

// Switches the demo uses to make things go wrong on purpose.
public sealed class Faults
{
    private int _failAfterPublish;

    public volatile bool BrokerDown;

    // The next dispatched message is published and then the dispatcher fails before marking it as sent.
    public void FailNextAfterPublish() => Interlocked.Exchange(ref _failAfterPublish, 1);

    public bool TakeFailAfterPublish() => Interlocked.Exchange(ref _failAfterPublish, 0) == 1;
}

// A stand-in for RabbitMQ, Azure Service Bus or SNS. It keeps every message it accepts in its own table,
// written on its own connection, so a message stays published when the caller's transaction rolls back.
// It hands the message to the consumer straight away; a real broker delivers asynchronously.
public sealed class FakeBroker(IDbContextFactory<AppDb> factory, Faults faults, InvoiceMailer consumer, ILogger<FakeBroker> log)
{
    public async Task PublishAsync(Guid messageId, string type, string payload, CancellationToken ct)
    {
        if (faults.BrokerDown)
        {
            throw new BrokerUnavailableException();
        }

        await using (var db = await factory.CreateDbContextAsync(ct))
        {
            db.BrokerMessages.Add(new BrokerMessage { MessageId = messageId, Type = type, Payload = payload });
            await db.SaveChangesAsync(ct);
        }

        try
        {
            await consumer.HandleAsync(messageId, type, payload, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The publisher is not told when a consumer fails. A real broker would redeliver.
            log.LogError(ex, "Consumer failed on message {MessageId}", messageId);
        }
    }
}
