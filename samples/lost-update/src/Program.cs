using LostUpdate;
using Microsoft.EntityFrameworkCore;

// "dotnet run -- demo" runs the demo client against an API that is already running.
if (args is ["demo", ..])
{
    await Demo.RunAsync(args.Length > 1 ? args[1] : "http://localhost:5087");
    return;
}

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDbContext<AppDb>(o => o.UseNpgsql(builder.Configuration.GetConnectionString("Db")));
builder.Services.AddProblemDetails();

var app = builder.Build();
app.UseExceptionHandler(new ExceptionHandlerOptions
{
    // In Development, minimal APIs throw BadHttpRequestException for a body they cannot bind.
    StatusCodeSelector = ex => ex is BadHttpRequestException bad ? bad.StatusCode : StatusCodes.Status500InternalServerError,
});
app.UseStatusCodePages();

// A fresh schema on every start keeps the demo repeatable. Real projects use migrations.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDb>();
    await db.Database.EnsureDeletedAsync();
    await db.Database.EnsureCreatedAsync();
    db.Invoices.AddRange(Enumerable.Range(1, 6).Select(id => new Invoice
    {
        Id = id, Number = $"INV-300{id}", Customer = "Acme Ltd", DueDate = new DateOnly(2026, 9, 30), Total = 100.00m,
    }));
    await db.SaveChangesAsync();
}

// The response carries the version as an ETag. The client sends it back in If-Match when it saves.
app.MapGet("/invoices/{id:int}", async (int id, HttpContext http, AppDb db, CancellationToken ct) =>
{
    if (await db.Invoices.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id, ct) is not { } invoice)
    {
        return Results.NotFound();
    }

    http.Response.Headers.ETag = invoice.ETag;
    return Results.Ok(invoice.ToResponse());
});

// Option 1: save whatever arrives. The last request to arrive wins, and the earlier ones are overwritten silently.
app.MapPut("/last-write-wins/invoices/{id:int}", async (int id, InvoiceUpdate update, AppDb db, CancellationToken ct) =>
{
    var changed = await db.Invoices.Where(i => i.Id == id).ExecuteUpdateAsync(s => s
        .SetProperty(i => i.Customer, update.Customer)
        .SetProperty(i => i.PoNumber, update.PoNumber)
        .SetProperty(i => i.DueDate, update.DueDate)
        .SetProperty(i => i.Total, update.Total), ct);

    return changed == 1 ? Results.NoContent() : Results.NotFound();
});

// Option 2: the client has to say which version it edited. The save happens only if that is still the current one.
app.MapPut("/invoices/{id:int}", async (int id, InvoiceUpdate update, HttpContext http, AppDb db, CancellationToken ct) =>
{
    var ifMatch = http.Request.Headers.IfMatch;
    if (ifMatch.Count == 0)
    {
        return Results.Problem(
            type: "https://errors.acme.example/precondition-required",
            title: "If-Match is required",
            detail: "Read the invoice first and send its ETag in an If-Match header.",
            statusCode: StatusCodes.Status428PreconditionRequired);
    }

    if (await db.Invoices.FirstOrDefaultAsync(i => i.Id == id, ct) is not { } invoice)
    {
        return Results.NotFound();
    }

    // If-Match: * means "any version, as long as the invoice exists". Anything else must be the version we hand out:
    // a quoted number. A weak ETag (W/"...") never matches, because If-Match uses strong comparison.
    var header = ifMatch.ToString().Trim();
    if (header != "*")
    {
        if (header.Length < 3 || header[0] != '"' || header[^1] != '"' || !uint.TryParse(header[1..^1], out var version))
        {
            return Stale();
        }

        // First check: the version the client edited against the one just read.
        // This one cannot be left to SaveChanges alone. If the row already holds the values in this request,
        // EF Core finds nothing to change, sends no UPDATE and so checks nothing: a stale request would get a 200.
        if (invoice.Version != version)
        {
            return Stale();
        }

        // Second check: someone can still save between the read above and the write below.
        // Telling EF Core which version the client edited makes the UPDATE carry "AND xmin = <that version>",
        // so the database compares in the same statement that writes.
        db.Entry(invoice).Property(i => i.Version).OriginalValue = version;
    }

    invoice.Customer = update.Customer;
    invoice.PoNumber = update.PoNumber;
    invoice.DueDate = update.DueDate;
    invoice.Total = update.Total;

    try
    {
        await db.SaveChangesAsync(ct);
    }
    catch (DbUpdateConcurrencyException)
    {
        return Stale();
    }

    http.Response.Headers.ETag = invoice.ETag; // the new version, so the client can go on editing
    return Results.Ok(invoice.ToResponse());

    static IResult Stale() => Results.Problem(
        type: "https://errors.acme.example/invoice-changed",
        title: "The invoice was changed by someone else",
        detail: "Read it again, apply your change to the current version and retry.",
        statusCode: StatusCodes.Status412PreconditionFailed);
});

// Option 3: when the change is relative ("add 10.00"), say so, and let one statement do it. No version is needed.
app.MapPost("/invoices/{id:int}/lines", async (int id, NewLine line, AppDb db, CancellationToken ct) =>
{
    var changed = await db.Invoices.Where(i => i.Id == id)
        .ExecuteUpdateAsync(s => s.SetProperty(i => i.Total, i => i.Total + line.Amount), ct);

    return changed == 1 ? Results.NoContent() : Results.NotFound();
});

app.MapGet("/health", () => Results.Ok());

app.Run();
