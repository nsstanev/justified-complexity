using Microsoft.EntityFrameworkCore;
using Npgsql;
using TenantIsolation;

// "dotnet run -- demo" runs the demo client against an API that is already running.
if (args is ["demo", ..])
{
    await Demo.RunAsync(args.Length > 1 ? args[1] : "http://localhost:5086");
    return;
}

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<TenantContext>();
builder.Services.AddScoped<TenantConnectionInterceptor>();

// Options 1 and 2 connect as the user that owns the tables, as many applications do.
builder.Services.AddDbContext<PlainDb>(o => o.UseNpgsql(builder.Configuration.GetConnectionString("Db")));
builder.Services.AddDbContext<FilteredDb>(o => o.UseNpgsql(builder.Configuration.GetConnectionString("Db")));

// Option 3 connects as payroll_api and tells the database the tenant on every connection.
builder.Services.AddDbContext<RlsDb>((services, o) => o
    .UseNpgsql(builder.Configuration.GetConnectionString("RlsDb"))
    .AddInterceptors(services.GetRequiredService<TenantConnectionInterceptor>()));

builder.Services.AddProblemDetails();

var app = builder.Build();
app.UseExceptionHandler();
app.UseStatusCodePages();

// A fresh schema on every start keeps the demo repeatable. Real projects use migrations.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<PlainDb>();
    await db.Database.EnsureDeletedAsync();
    await db.Database.EnsureCreatedAsync();
    await db.Database.ExecuteSqlRawAsync(Schema.RowLevelSecurity);

    db.Payslips.AddRange(
        Slip(Tenants.Acme, "Dana Petrova", 3120.50m), Slip(Tenants.Acme, "Ivan Stoyanov", 2890.00m), Slip(Tenants.Acme, "Hanna Lind", 4015.75m),
        Slip(Tenants.Globex, "Stefan Marin", 5230.00m), Slip(Tenants.Globex, "Joanna Reed", 3480.25m), Slip(Tenants.Globex, "Nathan Cole", 6100.00m));
    await db.SaveChangesAsync();

    static Payslip Slip(Guid tenant, string employee, decimal netPay) =>
        new() { Id = Guid.CreateVersion7(), TenantId = tenant, Employee = employee, Period = "2026-08", NetPay = netPay };
}

// ---- Option 1: every query carries its own WHERE ---------------------------------------------------------

app.MapGet("/manual/payslips", async (TenantContext tenant, PlainDb db, CancellationToken ct) =>
    await db.Payslips
        .Where(p => p.TenantId == tenant.TenantId)
        .OrderBy(p => p.Employee)
        .Select(p => new PayslipRow(p.Id, p.TenantId, p.Employee, p.Period, p.NetPay))
        .ToListAsync(ct));

// Written later, by someone else, in a hurry. The tenant is missing and nothing complains.
app.MapGet("/manual/payslips/search", async (string employee, PlainDb db, CancellationToken ct) =>
    await db.Payslips
        .Where(p => EF.Functions.ILike(p.Employee, $"%{employee}%"))
        .OrderBy(p => p.Employee)
        .Select(p => new PayslipRow(p.Id, p.TenantId, p.Employee, p.Period, p.NetPay))
        .ToListAsync(ct));

// ---- Option 2: a global query filter. Option 3: the same code on a login with row-level security ---------

MapFiltered<FilteredDb>(app, "/filtered");
MapFiltered<RlsDb>(app, "/rls");

// Tables with a tenant_id column and no row-level security.
app.MapGet("/admin/unprotected-tables", async (PlainDb db, CancellationToken ct) =>
    await db.Database.SqlQueryRaw<string>(Schema.UnprotectedTables).ToListAsync(ct));

app.MapGet("/health", () => Results.Ok());

app.Run();

static void MapFiltered<TDb>(WebApplication app, string prefix) where TDb : TenantFilteredDb
{
    // No WHERE on the tenant anywhere below: the filter adds it.
    app.MapGet($"{prefix}/payslips", async (TDb db, CancellationToken ct) =>
        await db.Payslips
            .OrderBy(p => p.Employee)
            .Select(p => new PayslipRow(p.Id, p.TenantId, p.Employee, p.Period, p.NetPay))
            .ToListAsync(ct));

    app.MapGet($"{prefix}/payslips/search", async (string employee, TDb db, CancellationToken ct) =>
        await db.Payslips
            .Where(p => EF.Functions.ILike(p.Employee, $"%{employee}%"))
            .OrderBy(p => p.Employee)
            .Select(p => new PayslipRow(p.Id, p.TenantId, p.Employee, p.Period, p.NetPay))
            .ToListAsync(ct));

    // Raw SQL that is not composed from the DbSet never meets the query filter.
    app.MapGet($"{prefix}/payslips/highest", async (TDb db, CancellationToken ct) =>
        await db.Database.SqlQueryRaw<PayslipRow>(Schema.HighestPaid).ToListAsync(ct));

    // The filter only reads. Nothing in EF Core stops a write into another tenant.
    app.MapPost($"{prefix}/payslips", async (NewPayslip body, TenantContext tenant, TDb db, CancellationToken ct) =>
    {
        if ((body.TenantId ?? tenant.TenantId) is not { } tenantId)
        {
            return Results.Problem(title: "No tenant", statusCode: StatusCodes.Status400BadRequest);
        }

        var payslip = new Payslip { Id = Guid.CreateVersion7(), TenantId = tenantId, Employee = body.Employee, Period = body.Period, NetPay = body.NetPay };
        db.Payslips.Add(payslip);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.InsufficientPrivilege })
        {
            // "new row violates row-level security policy": the database refused the write.
            return Results.Problem(title: "The row belongs to another tenant", statusCode: StatusCodes.Status403Forbidden);
        }

        return Results.Json(new PayslipRow(payslip.Id, payslip.TenantId, payslip.Employee, payslip.Period, payslip.NetPay), statusCode: StatusCodes.Status201Created);
    });
}
