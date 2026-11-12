# The invoice that was never sent

Companion sample for the article of the same name (*Justified Complexity*, nstanev.com).

A minimal ASP.NET Core API (.NET 10, EF Core, Npgsql) with PostgreSQL in Docker. It issues an invoice and has to tell a message broker about it, so that a consumer emails the invoice to the customer. It does that in three ways, and a small client makes each of them fail.

| Endpoint | How it publishes | What a failure leaves behind |
| --- | --- | --- |
| `POST /publish-after-commit/invoices` | Saves the invoice, then publishes | An invoice that nobody was told about |
| `POST /publish-before-commit/invoices` | Publishes inside the transaction, then commits | An event, and an email, for an invoice that does not exist |
| `POST /invoices` | Saves the invoice and an outbox row in one transaction; a background dispatcher publishes | Nothing lost and nothing invented; an event may be published twice |
| `GET /stats` | | Per invoice number: invoices saved, rows waiting in the outbox, events at the broker, emails |
| `GET /ordering?prefix=INV-2` | | How many outbox events reached the broker after an event written later |

No message library is used. The broker is faked inside the same process (`src/Broker.cs`): it stores every message it accepts in its own table, `broker_messages`, on its own connection, so a message stays published when the caller's transaction rolls back. The consumer (`src/Mailer.cs`) writes a row to `emails`; nothing is really sent.

## Run it in Docker

Requires Docker only. Start PostgreSQL and the API (the first run builds the image):

```bash
docker compose up -d --build
```

Run the demo client:

```bash
docker compose run --rm demo
```

Output:

```text
1. Publish after commit. The broker is down.
   POST /publish-after-commit/invoices   503
   INV-1001: invoices 1, outbox 0, events 0, emails 0   <- saved, and the customer never gets it

2. Publish before commit. The request fails between the publish and the commit.
   POST /publish-before-commit/invoices  500
   INV-1002: invoices 0, outbox 0, events 1, emails 1   <- emailed, and the invoice does not exist

3. Outbox. The broker is down.
   POST /invoices                        201
   INV-1003: invoices 1, outbox 1, events 0, emails 0   <- the invoice is issued; the event waits
   the broker comes back
   INV-1003: invoices 1, outbox 0, events 1, emails 1   <- delivered

4. Outbox. The dispatcher fails after publishing, before it marks the row as sent.
   POST /invoices                        201
   INV-1004: invoices 1, outbox 0, events 2, emails 1   <- published twice, emailed once

5. Outbox. 60 invoices pile up while the broker is down, then two dispatchers drain them.
   POST /invoices                        60 x 201
   invoices 60, outbox 0, events 60, emails 60, published twice 0
   events that reached the broker after a later one: 19 of 60
```

The client checks every line except the last one and exits with code 1 if something differs. The last number depends on timing; expect a different value on each run.

The database is reset only when the API starts. To run the demo again:

```bash
docker compose restart api
```

When you're done:

```bash
docker compose --profile demo down
```

## Or run the API with the .NET SDK

Requires Docker and the .NET 10 SDK. Start only the database:

```bash
docker compose up -d --wait db
```

```bash
dotnet run --project src
```

In a second terminal:

```bash
dotnet run --project src -- demo
```

## How the outbox endpoint works

1. **Write the message next to the data** (`src/Program.cs`). The invoice and an `outbox_messages` row go into one `SaveChangesAsync`, which is one transaction. Either both exist or neither does. The request never talks to the broker, so it answers 201 while the broker is down.
2. **A dispatcher publishes the rows** (`src/Outbox.cs`). Every 500 ms each worker opens a transaction, locks up to 20 unsent rows with `SELECT ... FOR UPDATE SKIP LOCKED`, publishes them, sets `processed_at` and commits. Two workers run in the sample; `SKIP LOCKED` gives each of them different rows.
3. **The consumer deduplicates** (`src/Mailer.cs`). If the dispatcher fails after publishing and before committing, the row is still unsent and is published again. The consumer inserts the message id into `inbox_messages` in the same transaction as its own work, and the primary key turns the second delivery into a no-op.

## Notes

- **What step 5 shows.** With two workers, each event is published once, but not in the order it was written: one worker holds rows 1 to 20 while the other publishes rows 21 to 40. If consumers need order, run one dispatcher, or partition the work by a key and keep one worker per partition.
- **What the faults simulate.** Steps 2 and 4 fail one request or one dispatcher pass; they do not kill the process. They exercise the same windows as a crash, but the sample cannot show recovery after a real restart, because the schema is recreated on every start.
- **The dispatcher keeps a transaction open while it publishes.** That is what holds the row locks. It is one connection per worker, not one per request, and a real publish needs a timeout so a stuck broker cannot hold the rows forever.
- **A failed publish stops the batch.** The row gets `attempts` and `last_error` and is retried on the next tick. A message that can never be published would block the rows behind it; a real system moves it aside after a number of attempts and alerts.
- **Sent rows are never deleted here.** The partial index `ix_outbox_messages_unsent` keeps the dispatcher's query fast as the table grows, but a real system deletes or archives sent rows.
- **The fake broker** delivers to the consumer inside `PublishAsync` and never delivers twice by itself. A real broker delivers asynchronously and can redeliver, which is one more reason for the inbox.
- **The consumer shares the API's database** to keep the sample small. A real consumer has its own, and its inbox table lives there.
- **Latency.** An event waits up to one polling interval. `Outbox:PollMilliseconds` and `Outbox:Workers` are in `src/appsettings.json`.
- The 500 in step 2 and the warnings from the outbox workers in steps 3 to 5 are logged on purpose.
- Ports `5084` (API) and `55436` (PostgreSQL) differ from the other samples, so you can run them side by side.
