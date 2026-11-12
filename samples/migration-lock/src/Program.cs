using Microsoft.EntityFrameworkCore;
using MigrationLock;
using Npgsql;

// "dotnet run -- demo" runs the demo client against an API that is already running.
if (args is ["demo", ..])
{
    await Demo.RunAsync(args.Length > 1 ? args[1] : "http://localhost:5085");
    return;
}

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("Db")!;
builder.Services.AddDbContext<AppDb>(o => o.UseNpgsql(connectionString));
builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString));
builder.Services.AddSingleton<Migrations>();
builder.Services.AddProblemDetails();

var app = builder.Build();
app.UseExceptionHandler();
app.UseStatusCodePages();

// A fresh schema and a large table on every start keep the demo repeatable. Real projects use migrations.
var rows = app.Configuration.GetValue("Seed:Rows", 2_000_000);
var ready = false;
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDb>();
    await db.Database.EnsureDeletedAsync();
    await db.Database.EnsureCreatedAsync();
    db.Database.SetCommandTimeout(300);
    await db.Database.ExecuteSqlAsync($"""
        INSERT INTO invoices (id, number, customer, total, status)
        SELECT g, 'INV-' || g, 'customer-' || (g % 5000), (g % 900) + 10, 'issued'
        FROM generate_series(1, {rows}) g
        """);
    await db.Database.ExecuteSqlRawAsync("ANALYZE invoices");
    ready = true;
}

// The two requests the API serves all day: a read and a write.
app.MapGet("/invoices/{id:int}", async (int id, AppDb db, CancellationToken ct) =>
    await db.Invoices.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id, ct) is { } invoice
        ? Results.Ok(new InvoiceResponse(invoice.Id, invoice.Number, invoice.Customer, invoice.Total, invoice.Status))
        : Results.NotFound());

app.MapPost("/invoices/{id:int}/pay", async (int id, AppDb db, CancellationToken ct) =>
    await db.Invoices.Where(i => i.Id == id).ExecuteUpdateAsync(s => s.SetProperty(i => i.Status, "paid"), ct) == 1
        ? Results.NoContent()
        : Results.NotFound());

// Starts a long-running read in the background and returns at once.
app.MapPost("/admin/report", (int? seconds, Migrations migrations, ILogger<Program> log) =>
{
    _ = Task.Run(async () =>
    {
        try
        {
            await migrations.RunReportAsync(seconds ?? 5);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "The report failed");
        }
    });
    return Results.Accepted();
});

// Runs one schema change and returns when it is done.
app.MapPost("/admin/migrations/{name}", async (string name, Migrations migrations, CancellationToken ct) =>
    Migrations.Names.Contains(name)
        ? Results.Ok(await migrations.RunAsync(name, ct))
        : Results.NotFound());

app.MapGet("/health", () => ready ? Results.Ok(new { rows }) : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));

app.Run();
