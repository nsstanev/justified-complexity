using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OutboxSample;

// "dotnet run -- demo" runs the demo client against an API that is already running.
if (args is ["demo", ..])
{
    await Demo.RunAsync(args.Length > 1 ? args[1] : "http://localhost:5084");
    return;
}

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDbContextFactory<AppDb>(o => o.UseNpgsql(builder.Configuration.GetConnectionString("Db")));
builder.Services.AddSingleton<Faults>();
builder.Services.AddSingleton<InvoiceMailer>();
builder.Services.AddSingleton<FakeBroker>();
builder.Services.AddHostedService<OutboxDispatcher>();
builder.Services.AddProblemDetails();

var app = builder.Build();
app.UseExceptionHandler(new ExceptionHandlerOptions
{
    StatusCodeSelector = ex => ex switch
    {
        // In Development, minimal APIs throw BadHttpRequestException for a body they cannot bind.
        BadHttpRequestException bad => bad.StatusCode,
        BrokerUnavailableException => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status500InternalServerError,
    },
});
app.UseStatusCodePages();

// A fresh schema on every start keeps the demo repeatable. Real projects use migrations.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDb>();
    await db.Database.EnsureDeletedAsync();
    await db.Database.EnsureCreatedAsync();
}

// Option 1: save the invoice, then publish. If the publish fails, the invoice exists and nobody is told.
app.MapPost("/publish-after-commit/invoices", async (IssueInvoiceRequest request, FakeBroker broker, AppDb db, CancellationToken ct) =>
{
    var invalid = InvoiceRules.Validate(request);
    if (invalid.Count > 0)
    {
        return Results.ValidationProblem(invalid);
    }

    var invoice = Invoice.From(request);
    db.Invoices.Add(invoice);
    await db.SaveChangesAsync(ct); // committed

    await broker.PublishAsync(Guid.CreateVersion7(), nameof(InvoiceIssued), JsonSerializer.Serialize(invoice.ToEvent(), JsonSerializerOptions.Web), ct);

    return Results.Json(invoice.ToResponse(), statusCode: StatusCodes.Status201Created);
});

// Option 2: publish inside the transaction, commit afterwards. If the commit does not happen,
// the event is out and the invoice is not there.
app.MapPost("/publish-before-commit/invoices", async (IssueInvoiceRequest request, bool? failBeforeCommit, FakeBroker broker, AppDb db, CancellationToken ct) =>
{
    var invalid = InvoiceRules.Validate(request);
    if (invalid.Count > 0)
    {
        return Results.ValidationProblem(invalid);
    }

    await using var transaction = await db.Database.BeginTransactionAsync(ct);

    var invoice = Invoice.From(request);
    db.Invoices.Add(invoice);
    await db.SaveChangesAsync(ct); // not committed yet

    await broker.PublishAsync(Guid.CreateVersion7(), nameof(InvoiceIssued), JsonSerializer.Serialize(invoice.ToEvent(), JsonSerializerOptions.Web), ct);

    if (failBeforeCommit == true)
    {
        // The request fails; the process stays up. It exercises the same window as a crash or a failed commit.
        throw new InvalidOperationException("Simulated failure: the event is published, the invoice is not committed.");
    }

    await transaction.CommitAsync(ct);

    return Results.Json(invoice.ToResponse(), statusCode: StatusCodes.Status201Created);
});

// Option 3: the outbox. The invoice and the message are saved together; the dispatcher (Outbox.cs) does the publishing.
app.MapPost("/invoices", async (IssueInvoiceRequest request, AppDb db, CancellationToken ct) =>
{
    var invalid = InvoiceRules.Validate(request);
    if (invalid.Count > 0)
    {
        return Results.ValidationProblem(invalid);
    }

    var invoice = Invoice.From(request);
    db.Invoices.Add(invoice);
    db.OutboxMessages.Add(OutboxMessage.From(invoice.ToEvent()));
    await db.SaveChangesAsync(ct); // one transaction: both rows or neither

    return Results.Json(invoice.ToResponse(), statusCode: StatusCodes.Status201Created);
});

// Demo switches.
app.MapPost("/faults/broker", (bool down, Faults faults) =>
{
    faults.BrokerDown = down;
    return Results.NoContent();
});

app.MapPost("/faults/dispatcher-fails-after-publish", (Faults faults) =>
{
    faults.FailNextAfterPublish();
    return Results.NoContent();
});

// Per invoice number: what the API saved, what waits in the outbox, what the broker accepted, what was emailed.
app.MapGet("/stats", async (AppDb db, CancellationToken ct) =>
{
    static string Number(string payload) => JsonSerializer.Deserialize<InvoiceIssued>(payload, JsonSerializerOptions.Web)!.Number;

    var invoices = await db.Invoices.Select(o => o.Number).ToListAsync(ct);
    var waiting = (await db.OutboxMessages.Where(m => m.ProcessedAt == null).Select(m => m.Payload).ToListAsync(ct)).Select(Number).ToList();
    var events = (await db.BrokerMessages.Select(m => m.Payload).ToListAsync(ct)).Select(Number).ToList();
    var emails = await db.Emails.Select(s => s.Number).ToListAsync(ct);

    return invoices.Concat(waiting).Concat(events).Concat(emails).Distinct().Order().Select(number => new StatsRow(
        number,
        invoices.Count(r => r == number),
        waiting.Count(r => r == number),
        events.Count(r => r == number),
        emails.Count(r => r == number)));
});

// How many events reached the broker later than an event written after them.
// The outbox order is ORDER BY id, the order the dispatcher reads in.
// "prefix" limits the report to invoice numbers that start with it.
app.MapGet("/ordering", async (string? prefix, AppDb db, CancellationToken ct) =>
{
    var written = await db.OutboxMessages.OrderBy(m => m.Id).Select(m => new { m.Id, m.Payload }).ToListAsync(ct);
    var position = written
        .Where(m => JsonSerializer.Deserialize<InvoiceIssued>(m.Payload, JsonSerializerOptions.Web)!.Number.StartsWith(prefix ?? ""))
        .Select((m, index) => (m.Id, index))
        .ToDictionary(x => x.Id, x => x.index);
    var arrived = await db.BrokerMessages.OrderBy(m => m.Id).Select(m => m.MessageId).ToListAsync(ct);

    var latest = -1;
    var outOfOrder = 0;
    foreach (var id in arrived)
    {
        if (!position.TryGetValue(id, out var index)) continue; // not sent through the outbox
        if (index < latest) outOfOrder++;
        latest = Math.Max(latest, index);
    }

    return new OrderingReport(arrived.Count(position.ContainsKey), outOfOrder);
});

app.Run();

public sealed record OrderingReport(int Events, int OutOfOrder);

public sealed record StatsRow(string Number, int Invoices, int OutboxWaiting, int Events, int Emails);
