# The deploy that locked the table

Companion sample for the article of the same name (*Justified Complexity*, nstanev.com).

A minimal ASP.NET Core API (.NET 10, EF Core, Npgsql) with PostgreSQL in Docker. It serves reads and writes on an `invoices` table of two million rows. A small client keeps sending requests while six schema changes run, three written the obvious way and three written the safe way, and counts the requests that time out.

| Endpoint | What it does |
| --- | --- |
| `GET /invoices/{id}` | Reads one invoice |
| `POST /invoices/{id}/pay` | Updates one invoice |
| `POST /admin/report?seconds=5` | Starts a long-running read of the table in the background |
| `POST /admin/migrations/{name}` | Runs one schema change: `add-column`, `add-column-safe`, `index`, `index-concurrently`, `check`, `check-not-valid` |

The schema changes are plain SQL in `src/Migrations.cs`.

## Run it in Docker

Requires Docker only. Start PostgreSQL and the API (the first run builds the image; every start loads two million rows, which takes some seconds):

```bash
docker compose up -d --build
```

Run the demo client (about 40 seconds):

```bash
docker compose run --rm demo
```

Output:

```text
Table invoices: 2,000,000 rows. Traffic: 5 reads and 5 writes per second, each with a 1 s timeout.

1. ALTER TABLE ... ADD COLUMN while a 5-second report is reading the table
   migration: 1 attempt(s), 4.7 s
   reads:     27 sent, 19 timed out, 3 slower than 500 ms
   writes:    27 sent, 19 timed out, 2 slower than 500 ms

2. The same, with lock_timeout = 300 ms and a retry
   migration: 6 attempt(s), 5.1 s
   reads:     27 sent, 0 timed out, 0 slower than 500 ms
   writes:    27 sent, 0 timed out, 0 slower than 500 ms

3. CREATE INDEX
   migration: 1 attempt(s), 3.7 s
   reads:     21 sent, 0 timed out, 0 slower than 500 ms
   writes:    21 sent, 14 timed out, 2 slower than 500 ms

4. CREATE INDEX CONCURRENTLY
   migration: 1 attempt(s), 3.9 s
   reads:     22 sent, 0 timed out, 0 slower than 500 ms
   writes:    22 sent, 0 timed out, 0 slower than 500 ms

5. ADD CONSTRAINT ... CHECK
   migration: 1 attempt(s), 1.5 s
   reads:     11 sent, 3 timed out, 3 slower than 500 ms
   writes:    11 sent, 3 timed out, 3 slower than 500 ms

6. ADD CONSTRAINT ... CHECK NOT VALID, then VALIDATE CONSTRAINT
   migration: 1 attempt(s), 1.5 s
   reads:     10 sent, 0 timed out, 0 slower than 500 ms
   writes:    10 sent, 0 timed out, 0 slower than 500 ms
```

The exact counts and times depend on the machine. The client checks the pattern (requests fail in steps 1, 3 and 5 and none fail in steps 2, 4 and 6) and exits with code 1 if it differs.

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

## What each pair shows

1. **`ADD COLUMN` behind a long read.** Adding a nullable column takes milliseconds, but it needs the strongest table lock, `ACCESS EXCLUSIVE`. The report holds the weakest one, so the `ALTER TABLE` waits. Every query that arrives after it waits behind it in the lock queue, reads included, until the report ends.
2. **`lock_timeout` and a retry.** The statement gives up after 300 ms and leaves the queue, so the requests behind it go ahead. It tries again 700 ms later and succeeds once the report is gone.
3. **`CREATE INDEX`** blocks every write for the whole build. Reads continue.
4. **`CREATE INDEX CONCURRENTLY`** builds the same index without blocking writes. A concurrent build that fails leaves an invalid index behind, so the sample looks at `pg_index.indisvalid` where that can happen: after a failed build and before a new one. It drops an invalid index with `DROP INDEX CONCURRENTLY`.
5. **`ADD CONSTRAINT ... CHECK`** checks all two million rows while it holds `ACCESS EXCLUSIVE`.
6. **`NOT VALID`, then `VALIDATE CONSTRAINT`.** The first statement enforces the rule for new rows and returns at once. The second checks the old rows under a lock that lets reads and writes through.

## Notes

- **The report is the trigger, not the cause.** Any transaction that has touched the table and is still open has the same effect: a slow query, a forgotten `BEGIN` in a console, a backup.
- **`lock_timeout` is set with `SET LOCAL`** inside the migration's transaction, so it never leaks to a pooled connection.
- **Step 4 does not use `lock_timeout`.** A concurrent index build that is interrupted leaves an invalid index, so it is better to let it wait, and to clean up if it fails.
- **Requests that time out in the client are still running on the server**, each holding a database connection until the lock is released. With more traffic than the demo sends, the connection pool runs out as well.
- **The demo uses raw SQL, not EF Core migrations.** In an EF Core migration the same statements go through `migrationBuilder.Sql(...)`, and `CREATE INDEX CONCURRENTLY` needs `suppressTransaction: true` because it cannot run inside a transaction.
- The schema is dropped, recreated and reloaded on every start so the demo is repeatable. `Seed:Rows` in `src/appsettings.json` sets the table size.
- Ports `5085` (API) and `55437` (PostgreSQL) differ from the other samples, so you can run them side by side.
