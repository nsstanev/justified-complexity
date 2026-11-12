# Two people edited the same invoice

Companion sample for the article of the same name (*Justified Complexity*, nstanev.com).

A minimal ASP.NET Core API (.NET 10, EF Core, Npgsql) with PostgreSQL in Docker. It saves an edited invoice in three ways, and a small client edits the same invoice from several places at once and counts the changes that survive.

| Endpoint | What it does with two edits of the same version |
| --- | --- |
| `GET /invoices/{id}` | Returns the invoice with its version in an `ETag` header |
| `PUT /last-write-wins/invoices/{id}` | Saves both. The second overwrites the first without a word |
| `PUT /invoices/{id}` | Requires `If-Match`. Saves the first; answers the second with 412 Precondition Failed. Without the header: 428 Precondition Required |
| `POST /invoices/{id}/lines` | Adds an amount to the total in one `UPDATE`. No version is needed |

The version is PostgreSQL's `xmin` system column, mapped as an EF Core row version.

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
1. Alice and Bob open the same invoice. Alice changes the due date, then Bob adds a PO number.
   no version check:  Alice 204, Bob 204. Saved: due 2026-09-30, PO PO-7781   <- Alice's change is gone
   If-Match:          Alice 200, Bob 412. Saved: due 2026-10-15, PO none   <- Bob is told to reload

2. 20 people each add 10.00 to an invoice of 100.00 at the same moment. The total should be 300.00.
   no version check:      20 x 204. Total: 110.00, 19 update(s) not applied
   If-Match:              1 x 200, 19 x 412. Total: 110.00, 19 update(s) not applied
   If-Match + retry:      20 x 200 after 86 attempts in total
                          Total: 300.00, 0 update(s) not applied
   one UPDATE statement:  20 x 204. Total: 300.00, 0 update(s) not applied

3. A save that does not say which version it edited
   PUT /invoices/2 without If-Match: 428
```

The number of attempts in the retry line changes from run to run. The client checks everything else and exits with code 1 if something differs.

The API is also reachable from the host at `http://localhost:5087`:

```bash
curl -i http://localhost:5087/invoices/1
```

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

## How the versioned endpoint works

1. **`GET` hands out the version.** The response carries `ETag: "<xmin>"`.
2. **`PUT` must bring it back.** No `If-Match` header: 428. The API refuses to guess which version the client edited.
3. **Two checks.** The endpoint compares the client's version with the row it just read, and answers 412 if they differ. Then it saves. `Version` is mapped as a row version, so EF Core adds `AND xmin = ...` with the version it read, which the first check has just shown to be the client's, and the database compares again in the statement that writes.
4. **A 412 is an instruction, not an error to swallow.** The client reads the invoice again, applies its change to the current version and retries, or shows the user what changed.

## Notes

- **Why the first check is needed.** If the row already holds exactly the values in the request, EF Core finds nothing to change and sends no `UPDATE`. With only the second check, a stale request would then get a 200, and a client that retries on 412 would count a change as saved that never was. An earlier version of this sample had that bug: seven of twenty stale saves were answered 200.
- **Two of the four lines in step 2 end at 110.00, and they are not the same.** Without a version check, 19 people believe they saved. With `If-Match`, 19 people are told they did not.
- **`xmin` changes on every write to the row**, including writes to columns the API does not map and writes by triggers. It is 32 bits and can change after a dump and restore. When that matters, use an explicit version column that the application increments.
- **`If-Match: *`** is accepted and means "any version, as long as the invoice exists". A weak ETag (`W/"..."`) never matches, because `If-Match` uses strong comparison.
- **The retry line makes 80 to 90 requests for 20 changes.** Optimistic concurrency is cheap when conflicts are rare. When many writers change the same row, prefer a relative change in one statement, as in the last line, or a queue.
- The schema is dropped and recreated on every start so the demo is repeatable. Use migrations in a real project.
- Ports `5087` (API) and `55439` (PostgreSQL) differ from the other samples, so you can run them side by side.
