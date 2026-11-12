using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace TenantIsolation;

public sealed class Payslip
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public required string Employee { get; set; }
    public required string Period { get; set; }
    public decimal NetPay { get; set; }
}

// A second tenant-owned table. Nobody wrote a policy for it; GET /admin/unprotected-tables finds it.
public sealed class LeaveRequest
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public required string Employee { get; set; }
}

public sealed record PayslipRow(Guid Id, Guid TenantId, string Employee, string Period, decimal NetPay);

// TenantId is the bug: the endpoint lets the caller say which tenant the row belongs to.
public sealed record NewPayslip(Guid? TenantId, string Employee, string Period, decimal NetPay);

public static class Tenants
{
    public static readonly Guid Acme = Guid.Parse("a0000000-0000-0000-0000-000000000001");
    public static readonly Guid Globex = Guid.Parse("b0000000-0000-0000-0000-000000000002");

    public static Guid? FromName(string? name) => name?.ToLowerInvariant() switch
    {
        "acme" => Acme,
        "globex" => Globex,
        _ => null,
    };
}

// Who the current request works for. The sample reads it from an X-Tenant header to stay small;
// a real API takes it from the access token and never from something the caller can type.
public sealed class TenantContext(IHttpContextAccessor accessor)
{
    public Guid? TenantId { get; } = Tenants.FromName(accessor.HttpContext?.Request.Headers["X-Tenant"].FirstOrDefault());
}

// Option 1: no help from the framework. Every query has to remember its own WHERE.
public sealed class PlainDb(DbContextOptions<PlainDb> options) : DbContext(options)
{
    public DbSet<Payslip> Payslips => Set<Payslip>();
    public DbSet<LeaveRequest> LeaveRequests => Set<LeaveRequest>();

    protected override void OnModelCreating(ModelBuilder b) => Schema.Map(b);
}

// Options 2 and 3 share this model: a global query filter adds the tenant to every LINQ query.
public abstract class TenantFilteredDb(DbContextOptions options, TenantContext tenant) : DbContext(options)
{
    // EF Core reads this property again for every query, so each request gets its own tenant.
    public Guid? CurrentTenant => tenant.TenantId;

    public DbSet<Payslip> Payslips => Set<Payslip>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        Schema.Map(b);
        b.Entity<Payslip>().HasQueryFilter(p => p.TenantId == CurrentTenant);
    }
}

// Option 2: the filter, on the same database login as option 1.
public sealed class FilteredDb(DbContextOptions<FilteredDb> options, TenantContext tenant) : TenantFilteredDb(options, tenant);

// Option 3: the filter, plus a login that the database itself restricts to one tenant's rows.
public sealed class RlsDb(DbContextOptions<RlsDb> options, TenantContext tenant) : TenantFilteredDb(options, tenant);

public static class Schema
{
    // Explicit names, because the policy and the raw SQL report use them.
    public static void Map(ModelBuilder b)
    {
        b.Entity<Payslip>(e =>
        {
            e.ToTable("payslips");
            e.Property(p => p.Id).HasColumnName("id");
            e.Property(p => p.TenantId).HasColumnName("tenant_id");
            e.Property(p => p.Employee).HasColumnName("employee");
            e.Property(p => p.Period).HasColumnName("period");
            e.Property(p => p.NetPay).HasColumnName("net_pay");
            e.HasIndex(p => p.TenantId);
        });

        b.Entity<LeaveRequest>(e =>
        {
            e.ToTable("leave_requests");
            e.Property(r => r.Id).HasColumnName("id");
            e.Property(r => r.TenantId).HasColumnName("tenant_id");
            e.Property(r => r.Employee).HasColumnName("employee");
        });
    }

    // Run once, as the table owner. The API's own login, payroll_api, owns nothing and cannot bypass the policy.
    public const string RowLevelSecurity = """
        DO $$ BEGIN
            IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'payroll_api') THEN
                CREATE ROLE payroll_api LOGIN PASSWORD 'demo';
            END IF;
        END $$;

        GRANT SELECT, INSERT, UPDATE, DELETE ON payslips TO payroll_api;

        ALTER TABLE payslips ENABLE ROW LEVEL SECURITY;

        -- USING decides which rows a query can see. WITH CHECK decides which rows it may write.
        -- With no tenant set, the setting is empty, NULLIF makes it NULL, and no row matches.
        CREATE POLICY payslips_same_tenant ON payslips TO payroll_api
            USING (tenant_id = NULLIF(current_setting('app.current_tenant', true), '')::uuid)
            WITH CHECK (tenant_id = NULLIF(current_setting('app.current_tenant', true), '')::uuid);
        """;

    // Tables that carry a tenant_id and have no row-level security. Run it as a test in CI.
    public const string UnprotectedTables = """
        SELECT c.relname::text AS "Value"
        FROM pg_class c
        JOIN pg_namespace n ON n.oid = c.relnamespace
        JOIN pg_attribute a ON a.attrelid = c.oid AND a.attname = 'tenant_id' AND NOT a.attisdropped
        WHERE n.nspname = 'public' AND c.relkind = 'r' AND NOT c.relrowsecurity
        ORDER BY 1
        """;

    // A report someone wrote in SQL. It has no tenant in it.
    public const string HighestPaid = """
        SELECT id AS "Id", tenant_id AS "TenantId", employee AS "Employee", period AS "Period", net_pay AS "NetPay"
        FROM payslips
        ORDER BY net_pay DESC
        LIMIT 10
        """;
}

// Tells PostgreSQL which tenant this connection works for, every time EF Core opens it.
// An empty value means "no tenant", and the policy then matches nothing.
public sealed class TenantConnectionInterceptor(TenantContext tenant) : DbConnectionInterceptor
{
    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await using var command = Command(connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var command = Command(connection);
        command.ExecuteNonQuery();
    }

    private DbCommand Command(DbConnection connection)
    {
        var command = connection.CreateCommand();
        command.CommandText = "SELECT set_config('app.current_tenant', @tenant, false)";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "tenant";
        parameter.Value = tenant.TenantId?.ToString() ?? "";
        command.Parameters.Add(parameter);
        return command;
    }
}
