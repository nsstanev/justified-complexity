using System.Text.Json;
using DuplicateCharge;
using Microsoft.EntityFrameworkCore;
using Npgsql;

// "dotnet run -- demo" runs the demo client against an API that is already running.
if (args is ["demo", ..])
{
    await Demo.RunAsync(args.Length > 1 ? args[1] : "http://localhost:5082");
    return;
}

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDbContextFactory<AppDb>(o => o.UseNpgsql(builder.Configuration.GetConnectionString("Db")));
builder.Services.AddSingleton<FakePaymentProvider>();
builder.Services.AddProblemDetails();

// How long a request may hold a key before a retry is allowed to take it over.
// Longer than your slowest request; the demo keeps it short so you don't have to wait.
var lockTimeout = TimeSpan.FromSeconds(builder.Configuration.GetValue("Idempotency:LockTimeoutSeconds", 2));

var app = builder.Build();
app.UseExceptionHandler(new ExceptionHandlerOptions
{
    // In Development, minimal APIs throw BadHttpRequestException for a body they cannot bind
    // (in Production they just answer 400). Without this, the handler would turn it into a 500.
    StatusCodeSelector = ex => ex is BadHttpRequestException bad ? bad.StatusCode : StatusCodes.Status500InternalServerError,
});
app.UseStatusCodePages();

// A fresh schema on every start keeps the demo repeatable. Real projects use migrations.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDb>();
    await db.Database.EnsureDeletedAsync();
    await db.Database.EnsureCreatedAsync();
}

// Option 1: every request charges the card. A retry is a second charge.
app.MapPost("/naive/payments", async (PaymentRequest request, int? delayMs, FakePaymentProvider provider, AppDb db, CancellationToken ct) =>
{
    var invalid = PaymentRules.Validate(request);
    if (invalid.Count > 0)
    {
        return Results.ValidationProblem(invalid);
    }

    var chargeId = await provider.ChargeAsync(request.OrderId, request.Amount, idempotencyKey: null, delayMs ?? 0, ct);

    var payment = new Payment { Id = Guid.CreateVersion7(), OrderId = request.OrderId, Amount = request.Amount, ProviderChargeId = chargeId };
    db.Payments.Add(payment);
    await db.SaveChangesAsync(ct);

    return Results.Json(new PaymentResponse(payment.Id, payment.OrderId, payment.Amount, chargeId), statusCode: StatusCodes.Status201Created);
});

// Option 2: the client names the operation with an Idempotency-Key, and the key travels all the way to the provider.
app.MapPost("/payments", async (PaymentRequest request, int? delayMs, bool? failAfterCharge, HttpContext http, FakePaymentProvider provider, AppDb db, CancellationToken ct) =>
{
    // This API's convention for the key: exactly one header, 1 to 255 characters, sent bare (as Stripe
    // accepts it) or as a quoted string (as the IETF draft writes it). Both forms name the same key.
    var headers = http.Request.Headers["Idempotency-Key"];
    var key = headers.Count == 1 ? headers[0]!.Trim() : "";
    if (key.Length >= 2 && key[0] == '"' && key[^1] == '"')
    {
        key = key[1..^1];
    }

    if (key.Length is 0 or > 255)
    {
        return Results.Problem(
            type: "https://errors.acme.example/idempotency-key-required",
            title: "Exactly one Idempotency-Key header is required",
            detail: "Send a unique value of at most 255 characters, such as a UUID, and repeat it on every retry of the same payment.",
            statusCode: StatusCodes.Status400BadRequest);
    }

    // Validation comes before the claim, so a request the contract rejects never uses up a key.
    var invalid = PaymentRules.Validate(request);
    if (invalid.Count > 0)
    {
        return Results.ValidationProblem(invalid);
    }

    // 1. Claim the key. Exactly one request gets to do the work.
    var claim = await IdempotencyStore.ClaimAsync(db, key, IdempotencyStore.Hash(request), lockTimeout, ct);
    switch (claim.Outcome)
    {
        case ClaimOutcome.Replay:
            return Replay(http, claim.Record!);

        case ClaimOutcome.InProgress:
            return Results.Problem(
                type: "https://errors.acme.example/payment-in-progress",
                title: "This payment is still being processed",
                detail: "Retry with the same Idempotency-Key in a moment.",
                statusCode: StatusCodes.Status409Conflict);

        case ClaimOutcome.Mismatch:
            return Results.Problem(
                type: "https://errors.acme.example/idempotency-key-reused",
                title: "Idempotency-Key was already used for a different request",
                detail: "Use a new key for a new payment.",
                statusCode: StatusCodes.Status422UnprocessableEntity);
    }

    // 2. Charge the card, passing a key derived from the client's key. If this request dies after
    //    this line, the retry reaches the provider with the same key and gets the same charge back.
    //    The provider account is shared by every caller of this API, so the downstream key names the
    //    operation as well as the client's key. A multi-tenant API adds the tenant: "{tenant}/payments/{key}".
    var providerKey = $"payments/{key}";
    var chargeId = await provider.ChargeAsync(request.OrderId, request.Amount, providerKey, delayMs ?? 0, ct);

    if (failAfterCharge == true)
    {
        // The request fails; the process stays up. It exercises the same window as a real crash.
        throw new InvalidOperationException("Simulated failure: the card is charged, the payment is not saved.");
    }

    // 3. Save the payment and the response in one transaction (a single SaveChanges).
    var payment = new Payment { Id = Guid.CreateVersion7(), OrderId = request.OrderId, Amount = request.Amount, ProviderChargeId = chargeId, IdempotencyKey = key };
    var body = JsonSerializer.Serialize(new PaymentResponse(payment.Id, payment.OrderId, payment.Amount, chargeId), JsonSerializerOptions.Web);

    db.Payments.Add(payment);
    var record = await db.IdempotencyKeys.SingleAsync(r => r.Key == key, ct);
    record.Status = IdempotencyRecord.Completed;
    record.ResponseStatus = StatusCodes.Status201Created;
    record.ResponseBody = body;
    try
    {
        await db.SaveChangesAsync(ct);
    }
    catch (DbUpdateException ex) when (ex.InnerException is PostgresException
    {
        SqlState: PostgresErrorCodes.UniqueViolation,
        ConstraintName: AppDb.PaymentsKeyIndex
    })
    {
        // A retry took the key over while this request was slow, and finished first. Answer with its response.
        db.ChangeTracker.Clear();
        return Replay(http, await db.IdempotencyKeys.AsNoTracking().SingleAsync(r => r.Key == key, ct));
    }

    return Results.Text(body, "application/json", statusCode: StatusCodes.Status201Created);
});

// What the API believes and what the provider charged, per order.
app.MapGet("/stats", async (AppDb db, CancellationToken ct) =>
{
    var payments = await db.Payments.GroupBy(p => p.OrderId).Select(g => new { OrderId = g.Key, Rows = g.Count() }).ToListAsync(ct);
    var charges = await db.ProviderCharges.GroupBy(c => c.OrderId).Select(g => new { OrderId = g.Key, Charges = g.Count(), Total = g.Sum(c => c.Amount) }).ToListAsync(ct);

    return charges.OrderBy(c => c.OrderId).Select(c => new
    {
        c.OrderId,
        PaymentRows = payments.FirstOrDefault(p => p.OrderId == c.OrderId)?.Rows ?? 0,
        ProviderCharges = c.Charges,
        TotalCharged = c.Total,
    });
});

app.Run();

// The stored response, byte for byte, plus a header that tells the client it is a replay.
static IResult Replay(HttpContext http, IdempotencyRecord record)
{
    http.Response.Headers["Idempotent-Replayed"] = "true";
    return Results.Text(record.ResponseBody!, "application/json", statusCode: record.ResponseStatus);
}
