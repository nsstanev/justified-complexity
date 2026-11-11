# A database constraint beats an async validator

Companion sample for the article of the same name (*Justified Complexity*, nstanev.com).

A minimal ASP.NET Core API (.NET 10, EF Core, Npgsql, FluentValidation) with PostgreSQL in Docker. It exposes the same signup twice:

| Endpoint | Guard against duplicate emails |
| --- | --- |
| `POST /naive/users` | FluentValidation `MustAsync` lookup only; the `naive_users` table has no unique index |
| `POST /users` | Shape validation in code, unique index `ix_users_email` in the database, unique violation (`23505`) turned into `409 Conflict` |
| `GET /stats` | Rows per email in each table |

## Run it in Docker

Requires Docker only. Start PostgreSQL and the API (the first run builds the image):

```bash
docker compose up -d --build
```

Fire 20 concurrent signups for the same email at both endpoints:

```bash
docker compose run --rm race
```

The API is also reachable from the host at `http://localhost:5080`.

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
dotnet run --project src -- race
```

Typical output:

```text
POST /naive/users  20 concurrent signups for dana+9acca3dd@acme.example: 18 x 201, 2 x 409
POST /users        20 concurrent signups for dana+9acca3dd@acme.example: 1 x 201, 19 x 409

Rows per email (GET /stats):
{"naive":[{"email":"dana+9acca3dd@acme.example","rows":18}],"constrained":[{"email":"dana+9acca3dd@acme.example","rows":1}]}
```

The client makes up a fresh address for every run and uses it for both endpoints, so you can run it again against the same API; `/stats` then lists one line per run.

The naive numbers vary from run to run (anything from a few duplicates to all 20). The constrained ones stay at one 201 and nineteen 409s, as long as every request completes: the address is new, so exactly one insert can commit.

When you're done:

```bash
docker compose --profile race down
```

## Notes

- The race client adds `?delayMs=30` to both endpoints. It stands in for real work between the check and the insert and makes the race show up on every run. Without it the race still happens, just less often, which is exactly why it reaches production.
- The schema is dropped and recreated on every start (`EnsureDeleted` / `EnsureCreated`) so the demo is repeatable. Use migrations in a real project.
- EF Core logs each unique violation at Error level even though the endpoint handles it. Filter the `Microsoft.EntityFrameworkCore.Update` category if that noise matters to you.
- PostgreSQL listens on port `55432` to avoid clashing with a local install.
- In Docker, the API container gets its connection string and listen URL from environment variables in `compose.yaml` (`ConnectionStrings__Db`, `Urls`), which override `appsettings.json`. The image is a two-stage build (SDK, then the ASP.NET Core runtime) and runs as a non-root user.
- The race client waits for the API to answer before firing, because the API recreates its schema on start.
