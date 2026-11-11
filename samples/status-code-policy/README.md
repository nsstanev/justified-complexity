# 400 or 422?

Companion sample for the article of the same name (*Justified Complexity*, nstanev.com).

A minimal ASP.NET Core API (.NET 10, EF Core, Npgsql, FluentValidation) with PostgreSQL in Docker. One endpoint, `POST /users`, refuses a signup in three different ways. It never picks a status code itself: it returns an error from the catalogue in `src/Errors.cs`, and the catalogue maps each error to a status code and a stable Problem Details `type`.

| Request body | Status | `type` (after `https://errors.acme.example/`) | Why |
| --- | --- | --- | --- |
| `{"email":"not-an-email"}` | 400 | `validation-failed` | The API contract says the request is wrong |
| `{"email":"dana@acme.example"}`, sent twice | 201, then 409 | `email-taken` | A valid request that the current state refuses |
| `{"email":"dana@corp.example"}` | 422 | `signup-domain-closed` | A valid request that a business rule refuses |

## Run it in Docker

Requires Docker only. Start PostgreSQL and the API (the first run builds the image):

```bash
docker compose up -d --build
```

The API listens on `http://localhost:5081`. Send one request per row of the table:

```bash
curl -i -X POST http://localhost:5081/users -H "Content-Type: application/json" -d '{"email":"not-an-email"}'
```

```bash
curl -i -X POST http://localhost:5081/users -H "Content-Type: application/json" -d '{"email":"dana@acme.example"}'
```

```bash
curl -i -X POST http://localhost:5081/users -H "Content-Type: application/json" -d '{"email":"dana@corp.example"}'
```

Run the second one twice: the first call returns 201, the second 409.

In PowerShell, use `curl.exe` and escape the inner quotes, for example `-d '{\"email\":\"not-an-email\"}'`.

Typical responses (trace IDs removed):

```text
HTTP/1.1 400 Bad Request
Content-Type: application/problem+json

{"type":"https://errors.acme.example/validation-failed","title":"One or more fields are invalid","status":400,"errors":{"Email":["'Email' is not a valid email address."]}}
```

```text
HTTP/1.1 409 Conflict
Content-Type: application/problem+json

{"type":"https://errors.acme.example/email-taken","title":"Email already registered","status":409,"detail":"An account with this email already exists."}
```

```text
HTTP/1.1 422 Unprocessable Entity
Content-Type: application/problem+json

{"type":"https://errors.acme.example/signup-domain-closed","title":"Signups are closed for this domain","status":422,"detail":"Accounts for corp.example are created by the organisation's administrator."}
```

When you're done:

```bash
docker compose down
```

## Or run the API with the .NET SDK

Requires Docker and the .NET 10 SDK. Start only the database:

```bash
docker compose up -d --wait db
```

```bash
dotnet run --project src
```

## Notes

- The closed domains are a setting, `Signup:ClosedDomains` in `src/appsettings.json`.
- Broken JSON and unknown routes never reach the endpoint. `UseExceptionHandler` and `UseStatusCodePages` in `Program.cs` give those failures a Problem Details body as well.
- The 409 comes from the unique index `ix_users_email`, not from a lookup. The [constraint-beats-validator](../constraint-beats-validator) sample shows why.
- The schema is dropped and recreated on every start (`EnsureDeleted` / `EnsureCreated`) so the demo is repeatable. Use migrations in a real project.
- EF Core logs each unique violation at Error level even though the endpoint handles it. Filter the `Microsoft.EntityFrameworkCore.Update` category if that noise matters to you.
- Ports `5081` (API) and `55433` (PostgreSQL) differ from the other samples, so you can run them side by side.
