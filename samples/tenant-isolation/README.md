# The query that returned another company's payslips

Companion sample for the article of the same name (*Justified Complexity*, nstanev.com).

A minimal ASP.NET Core API (.NET 10, EF Core, Npgsql) with PostgreSQL in Docker. It is a payroll service with two tenants, Acme and Globex, in one `payslips` table. It keeps them apart in three ways, and a small client sends Acme's tenant header and counts the rows it gets that belong to Globex.

| Endpoints | How tenants are kept apart | Where it leaks |
| --- | --- | --- |
| `/manual/payslips`, `/manual/payslips/search` | Every query has its own `WHERE tenant_id = ...` | The one query that forgot it |
| `/filtered/payslips`, `.../search`, `.../highest`, `POST` | An EF Core global query filter | Raw SQL, writes, and code that runs with no tenant |
| `/rls/payslips`, `.../search`, `.../highest`, `POST` | The same filter, on a database login restricted by PostgreSQL row-level security | Nowhere in this sample |
| `GET /admin/unprotected-tables` | | Lists tables that have a `tenant_id` column and no row-level security |

The tenant comes from an `X-Tenant` header (`acme` or `globex`) to keep the sample small. A real API takes it from the access token.

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
Two tenants, Acme and Globex, with 3 payslips each. Every request below is made as Acme.

1. A WHERE clause in every query
   GET  /manual/payslips             3 row(s), 0 from another tenant
   GET  /manual/payslips/search      6 row(s), 3 from another tenant   <- this query forgot the WHERE

2. An EF Core global query filter
   GET  /filtered/payslips           3 row(s), 0 from another tenant
   GET  /filtered/payslips/search    3 row(s), 0 from another tenant
   GET  /filtered/payslips/highest   6 row(s), 3 from another tenant   <- raw SQL does not go through the filter

3. The same code on a database login with row-level security
   GET  /rls/payslips                3 row(s), 0 from another tenant
   GET  /rls/payslips/search         3 row(s), 0 from another tenant
   GET  /rls/payslips/highest        3 row(s), 0 from another tenant   <- the same raw SQL

4. A write that names another tenant (the body says Globex)
   POST /rls/payslips                403   <- the database refused the row
   POST /filtered/payslips           201   <- a payslip planted in Globex

5. No tenant at all, as in a background job
   GET  /filtered/payslips/highest   7 row(s) from 2 tenant(s)   <- every tenant's rows
   GET  /rls/payslips/highest        0 row(s) from 0 tenant(s)   <- nothing: no tenant, no rows

6. Tables with a tenant_id column and no row-level security: leave_requests
```

The client checks every line and exits with code 1 if something differs.

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

## How the row-level security variant works

1. **A login that owns nothing.** The tables are created by the owner. The API connects as `payroll_api`, which has only `SELECT`, `INSERT`, `UPDATE` and `DELETE` on `payslips`. Table owners and superusers are not subject to policies by default, so an application that connects as the owner gets no protection from them.
2. **A policy on the table** (`Schema.RowLevelSecurity` in `src/Data.cs`). `USING` limits the rows a statement can see and `WITH CHECK` limits the rows it may write, both to `tenant_id = current_setting('app.current_tenant')`.
3. **The tenant is set on every connection** (`TenantConnectionInterceptor`). Each time EF Core opens a connection, the interceptor runs `set_config('app.current_tenant', ...)`, with an empty value when there is no tenant.
4. **No tenant means no rows.** The policy turns the empty value into `NULL`, and `tenant_id = NULL` matches nothing. A job that forgets to set a tenant reads nothing and can write nothing.

## Notes

- **The query filter stays in option 3.** It keeps LINQ queries correct and readable; row-level security is the layer that still holds when a query does not go through it.
- **Why the raw SQL leaks in option 2.** `Database.SqlQueryRaw<T>` runs the SQL as written. A global query filter is applied only to queries composed from the filtered entity's `DbSet`, including `FromSql` on it.
- **Why the tenant is set per connection, not per transaction.** The interceptor sets the value every time a connection is opened, including to "no tenant", so a pooled connection never carries the previous request's tenant. With a pooler in transaction mode, such as PgBouncer, a session setting is not safe: set it with `set_config(..., true)` inside each transaction.
- **The `POST` endpoints contain a bug on purpose:** they accept a tenant id in the body. Option 2 writes the row into the other tenant; option 3 gets error `42501` from PostgreSQL and answers 403.
- **Step 6 is meant to become a test.** `Schema.UnprotectedTables` lists every table with a `tenant_id` column and no row-level security. `leave_requests` is in the sample without a policy so that the check has something to find.
- **Not shown:** views (they run with the view owner's rights unless created with `security_invoker`), `BYPASSRLS` roles for support tooling, and the cost of the policy on large tables, where `tenant_id` has to lead the indexes.
- The schema is dropped and recreated on every start so the demo is repeatable. The role `payroll_api` is created once per PostgreSQL instance.
- The 403 in step 4 is logged by EF Core as a failed command. That is the refusal.
- Ports `5086` (API) and `55438` (PostgreSQL) differ from the other samples, so you can run them side by side.
