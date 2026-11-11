using FluentValidation;
using HandlersWithDecorators;
using MediatR;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;

// "dotnet run -- demo" runs the demo client against an API that is already running.
if (args is ["demo", ..])
{
    await Demo.RunAsync(args.Length > 1 ? args[1] : "http://localhost:5083");
    return;
}

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDbContext<AppDb>(o => o.UseNpgsql(builder.Configuration.GetConnectionString("Db")));
builder.Services.AddScoped<IValidator<PlaceOrder>, PlaceOrderValidator>();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ProblemExceptionHandler>();

// Option 1: MediatR. Handlers are found by scanning; the behaviors run around every request, in this order.
builder.Services.AddMediatR(cfg =>
{
    cfg.RegisterServicesFromAssemblyContaining<PlaceOrder>();
    cfg.AddOpenBehavior(typeof(LoggingBehavior<,>));
    cfg.AddOpenBehavior(typeof(ValidationBehavior<,>));
    cfg.AddOpenBehavior(typeof(TransactionBehavior<,>));
});

// Options 2 and 3: the handler itself, and the same handler wrapped in three decorators (see Handlers.cs).
builder.Services.AddHandler<PlaceOrder, OrderPlaced, PlaceOrderHandler>();

var app = builder.Build();
app.UseExceptionHandler();
app.UseStatusCodePages();

// A fresh schema on every start keeps the demo repeatable. Real projects use migrations.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDb>();
    await db.Database.EnsureDeletedAsync();
    await db.Database.EnsureCreatedAsync();
    await db.SeedAsync();
}

// Option 1: send the request to MediatR. Logging, validation and a transaction happen in the pipeline.
app.MapPost("/mediatr/orders", async (PlaceOrderBody body, ISender sender, CancellationToken ct) =>
    Created(await sender.Send(ToCommand(Sources.MediatR, body), ct)));

// Option 2: MediatR removed, the handler called directly. The business logic is identical.
// The logging, the validation and the transaction left with the pipeline, and nothing says so.
app.MapPost("/plain/orders", async (PlaceOrderBody body, PlaceOrderHandler handler, CancellationToken ct) =>
    Created(await handler.HandleAsync(ToCommand(Sources.Plain, body), ct)));

// Option 3: ask for the handler by its interface and get it wrapped in the decorators.
app.MapPost("/orders", async (PlaceOrderBody body, IHandler<PlaceOrder, OrderPlaced> handler, CancellationToken ct) =>
    Created(await handler.HandleAsync(ToCommand(Sources.Decorated, body), ct)));

// What each endpoint left in the database.
app.MapGet("/stats", async (AppDb db, CancellationToken ct) =>
{
    var orders = await db.Orders.GroupBy(o => o.Source).Select(g => new { Source = g.Key, Count = g.Count() }).ToListAsync(ct);
    var stock = await db.Stock.Where(s => s.Sku == "widget").ToListAsync(ct);

    return Sources.All.Select(source => new
    {
        Source = source,
        Orders = orders.FirstOrDefault(o => o.Source == source)?.Count ?? 0,
        WidgetsInStock = stock.Single(s => s.Source == source).Available,
    });
});

app.Run();

static PlaceOrder ToCommand(string source, PlaceOrderBody body) => new(source, body.CustomerId, body.Lines ?? []);

static IResult Created(OrderPlaced placed) => Results.Created($"/orders/{placed.OrderId}", placed);

// One place turns the exceptions the use cases throw into Problem Details. Anything else stays a 500.
internal sealed class ProblemExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext http, Exception exception, CancellationToken ct)
    {
        IResult? result = exception switch
        {
            // The contract says the request is wrong.
            ValidationException invalid => Results.ValidationProblem(
                invalid.Errors.GroupBy(e => e.PropertyName).ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray())),

            // The request is fine; the current state of the shelf rules it out.
            OutOfStockException outOfStock => Results.Problem(
                title: "Not enough stock",
                detail: outOfStock.Message,
                statusCode: StatusCodes.Status409Conflict),

            // In Development, minimal APIs throw for a body they cannot bind. Keep its own status.
            BadHttpRequestException bad => Results.Problem(statusCode: bad.StatusCode),

            _ => null,
        };

        if (result is null)
        {
            return false;
        }

        await result.ExecuteAsync(http);
        return true;
    }
}
