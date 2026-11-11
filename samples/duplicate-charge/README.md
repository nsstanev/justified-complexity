# The duplicate charge

Companion sample for the article of the same name (*Justified Complexity*, nstanev.com).

A minimal ASP.NET Core API (.NET 10, EF Core, Npgsql) with PostgreSQL in Docker. It takes the same payment in two ways, and a small client plays a customer with a flaky connection against both.

| Endpoint | What a retry does |
| --- | --- |
| `POST /naive/payments` | Charges the card again. Every request is a new payment. |
| `POST /payments` | Requires an `Idempotency-Key` header. A retry with the same key gets the first response back and the card is charged once. |
| `GET /stats` | Payment rows and provider charges per order |

The payment provider is faked inside the same process (`src/Provider.cs`). It stores its charges in its own table, `provider_charges`, and commits them on its own connection, so a charge survives when the API request fails afterwards, as it would with a real provider.

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
1. No key: the client retries a payment of 49.90 five times at once
   POST /naive/payments  5 x 201

2. Same retries, with one Idempotency-Key
   POST /payments        1 x 201, 4 x 409
   retry after it finished: 201 (replayed)

3. The same key with a different amount
   POST /payments        422

4. The API crashes after the card is charged, before the payment is saved
   first attempt:        500
   immediate retry:      409
   retry after 2.5 s:    201

What the API saved and what the provider charged (GET /stats):
   order-1001: 5 payment row(s), 5 charge(s), 249.50 charged
   order-1002: 1 payment row(s), 1 charge(s), 49.90 charged
   order-1003: 1 payment row(s), 1 charge(s), 49.90 charged
```

The API is also reachable from the host at `http://localhost:5082`:

```bash
curl -i -X POST http://localhost:5082/payments -H "Content-Type: application/json" -H "Idempotency-Key: 0f2b6c1e-8d4a-4f0b-9a57-3c1d2e4f5a6b" -d '{"orderId":"order-2001","amount":49.90}'
```

Run it twice: the second response is the same 201 with the same body, plus the header `Idempotent-Replayed: true`.

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

## How the keyed endpoint works

1. **Claim the key** (`src/Idempotency.cs`). One `INSERT ... ON CONFLICT` statement on the `idempotency_keys` table decides which request owns the key. The others see why they lost: the work is done (replay the stored response), still running (409), or the key belongs to a different request body (422). A missing key is a 400.
2. **Charge the card, passing the same key to the provider.** If the API dies after the charge, the retry reaches the provider with the same key and gets the original charge back.
3. **Save the payment and the response in one transaction.** A unique index on `payments.idempotency_key` is the final guard if two workers ever finish the same key.

## Notes

- `Idempotency:LockTimeoutSeconds` in `src/appsettings.json` is how long a request may hold a key before a retry can take it over. The demo uses 2 seconds so step 4 is quick to watch. In a real system make it longer than your slowest request.
- `?delayMs=300` makes the fake provider slow enough for the retries to overlap. `?crashAfterCharge=true` throws after the charge and before the payment is saved.
- Keys are never deleted here. A real system removes them after a retention period that its clients know about.
- The schema is dropped and recreated on every start (`EnsureDeleted` / `EnsureCreated`) so the demo is repeatable. Use migrations in a real project.
- The 500 in step 4 is logged as an unhandled exception. That is the simulated crash.
- Ports `5082` (API) and `55434` (PostgreSQL) differ from the other samples, so you can run them side by side.
