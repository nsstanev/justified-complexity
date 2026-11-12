# Justified Complexity

Companion code for *Justified Complexity: Building Backends One Decision at a Time* and the articles at [nstanev.com](https://nstanev.com).

Each sample shows a backend decision you can run: the simple version, where it breaks, and the fix that justifies the extra complexity. Every sample is self-contained, with its own README and a `compose.yaml`, so Docker is all you need to try it.

## Samples

| Sample | What it shows | Article |
| --- | --- | --- |
| [constraint-beats-validator](samples/constraint-beats-validator) | Async "is this email taken?" validation lets concurrent duplicates through; a unique index plus a 409 doesn't | A database constraint beats an async validator |
| [status-code-policy](samples/status-code-policy) | One endpoint refuses a signup three ways (400, 409, 422) through one error catalogue with stable Problem Details types | 400 or 422? |
| [duplicate-charge](samples/duplicate-charge) | A retried payment charges the card again; an Idempotency-Key, passed on to the payment provider, makes the retry return the first response | The duplicate charge |
| [handlers-with-decorators](samples/handlers-with-decorators) | The same order through MediatR, a plain handler and a handler with decorators: calling the handler directly drops validation, logging and the transaction; three decorators bring them back | You don't need MediatR, but you do need decorators |
| [outbox](samples/outbox) | An invoice and its event published three ways: after the commit (the event is lost), before the commit (an event for an invoice that does not exist) and through an outbox with a polling dispatcher and an inbox on the consumer | The invoice that was never sent |
| [migration-lock](samples/migration-lock) | Six schema changes run under steady traffic on a two-million-row table: `ADD COLUMN`, `CREATE INDEX` and `ADD CONSTRAINT` the obvious way make requests time out; `lock_timeout` with a retry, `CONCURRENTLY` and `NOT VALID` don't | The deploy that locked the table |
| [tenant-isolation](samples/tenant-isolation) | Two tenants in one table, kept apart by a `WHERE` in every query, by an EF Core global query filter and by PostgreSQL row-level security; a client counts the rows that leak through each | The query that returned another company's payslips |
| [lost-update](samples/lost-update) | Two editors and twenty simultaneous edits of one invoice: last write wins loses changes; `ETag` and `If-Match` refuse the stale save with 412 | Two people edited the same invoice |

## Stack

.NET 10, ASP.NET Core Minimal APIs, EF Core, PostgreSQL, Docker Compose.

## License

Code in this repository is licensed under [MIT](LICENSE). The text of the book *Justified Complexity* and the articles at nstanev.com are © Nikolay Stanev, all rights reserved.
