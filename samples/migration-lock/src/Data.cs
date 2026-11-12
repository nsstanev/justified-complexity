using Microsoft.EntityFrameworkCore;

namespace MigrationLock;

public sealed class Invoice
{
    public int Id { get; set; }
    public required string Number { get; set; }
    public required string Customer { get; set; }
    public decimal Total { get; set; }
    public required string Status { get; set; }
}

public sealed record InvoiceResponse(int Id, string Number, string Customer, decimal Total, string Status);

public sealed class AppDb(DbContextOptions<AppDb> options) : DbContext(options)
{
    public DbSet<Invoice> Invoices => Set<Invoice>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        // Explicit names, because Migrations.cs changes this table with hand-written SQL.
        b.Entity<Invoice>(e =>
        {
            e.ToTable("invoices");
            e.Property(i => i.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(i => i.Number).HasColumnName("number");
            e.Property(i => i.Customer).HasColumnName("customer");
            e.Property(i => i.Total).HasColumnName("total");
            e.Property(i => i.Status).HasColumnName("status");
        });
    }
}
