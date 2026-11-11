# You don't need MediatR, but you do need decorators

Companion sample for the article of the same name (*Justified Complexity*, nstanev.com).

A minimal ASP.NET Core API (.NET 10, EF Core, Npgsql, FluentValidation) with PostgreSQL in Docker. It places the same order through three endpoints that share one piece of business logic, `PlaceOrderHandler`.

| Endpoint | How the handler is reached | Logging, validation, transaction |
| --- | --- | --- |
| `POST /mediatr/orders` | `ISender.Send`, MediatR 12.5.0 | Three pipeline behaviors (`src/Behaviors.cs`) |
| `POST /plain/orders` | The handler is injected and called directly | None. They left with the pipeline |
| `POST /orders` | `IHandler<PlaceOrder, OrderPlaced>` is injected | Three decorators (`src/Handlers.cs`) |
| `GET /stats` | | Orders saved and widgets in stock, per endpoint |

Placing an order does two writes: it saves the order, then reserves stock for each line. Each endpoint has its own shelf with ten widgets and no gadgets.

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
1. An order for -5 widgets (the request is invalid)
   MediatR with behaviors   POST /mediatr/orders 400
   Handler called directly  POST /plain/orders   201
   Handler with decorators  POST /orders         400

2. An order for 1 widget and 1 gadget (gadgets are out of stock)
   MediatR with behaviors   POST /mediatr/orders 409
   Handler called directly  POST /plain/orders   409
   Handler with decorators  POST /orders         409

What each endpoint left behind (GET /stats). Each started with 10 widgets and no orders:
   mediatr    0 order(s) saved, 10 widgets in stock
   plain      2 order(s) saved, 14 widgets in stock
   decorated  0 order(s) saved, 10 widgets in stock
```

What happened on the plain endpoint:

- The order for -5 widgets was accepted, saved, and added five widgets to the shelf. Nothing validated it.
- The second order answered 409, as it should. But the order row and the reservation of one widget were already written, and nothing rolled them back.
- Neither request was logged by the application. Compare with the other two:

```bash
docker compose logs api
```

The API is also reachable from the host at `http://localhost:5083`:

```bash
curl -i -X POST http://localhost:5083/orders -H "Content-Type: application/json" -d '{"customerId":"customer-1","lines":[{"sku":"widget","quantity":2}]}'
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

## Where to look

- `src/Handlers.cs`: the whole replacement for MediatR. One interface, three decorators and `AddHandler`, which wraps a handler in them. The order of the decorators is written out in one place: logging outermost, then validation, then the transaction.
- `src/Behaviors.cs`: the same three jobs as MediatR pipeline behaviors. The bodies are identical to the decorators.
- `src/Orders.cs`: the request, its validator and `PlaceOrderHandler`, which knows nothing about any of the above.
- `src/Program.cs`: the three endpoints, and one `IExceptionHandler` that turns a validation failure into a 400 and an out-of-stock failure into a 409.

## Notes

- **MediatR version.** The sample pins MediatR 12.5.0, the last release under the Apache-2.0 licence. Version 13 and later are commercially licensed and log a warning without a licence key.
- **Registration without a library.** `AddHandler` composes the decorators with a factory lambda, so the sample needs nothing beyond the built-in container. With many handlers, [Scrutor](https://github.com/khellang/Scrutor)'s `Decorate(typeof(IHandler<,>), typeof(LoggingDecorator<,>))` does the same for every handler at once. Its order is the reverse of MediatR's: the last `Decorate` call is the outermost.
- **Validation runs before the transaction is opened**, so an invalid request never takes a database connection.
- **The out-of-stock check is a single `UPDATE ... WHERE available >= quantity`**, so two orders cannot both take the last widget. A negative quantity passes that check, which is why the unvalidated endpoint adds stock.
- The schema is dropped and recreated on every start (`EnsureDeleted` / `EnsureCreated`) so the demo is repeatable. Use migrations in a real project.
- Ports `5083` (API) and `55435` (PostgreSQL) differ from the other samples, so you can run them side by side.
