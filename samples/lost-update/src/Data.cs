using Microsoft.EntityFrameworkCore;

namespace LostUpdate;

public sealed class Invoice
{
    public int Id { get; set; }
    public required string Number { get; set; }
    public required string Customer { get; set; }
    public string? PoNumber { get; set; }
    public DateOnly DueDate { get; set; }
    public decimal Total { get; set; }

    // PostgreSQL's xmin system column: it changes every time the row is written.
    public uint Version { get; set; }

    public InvoiceResponse ToResponse() => new(Id, Number, Customer, PoNumber, DueDate, Total);

    public string ETag => $"\"{Version}\"";
}

public sealed record InvoiceResponse(int Id, string Number, string Customer, string? PoNumber, DateOnly DueDate, decimal Total);

// PUT replaces the editable fields. The client sends back the whole form, including the fields it did not touch.
public sealed record InvoiceUpdate(string Customer, string? PoNumber, DateOnly DueDate, decimal Total);

public sealed record NewLine(decimal Amount);

public sealed class AppDb(DbContextOptions<AppDb> options) : DbContext(options)
{
    public DbSet<Invoice> Invoices => Set<Invoice>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Invoice>(e =>
        {
            e.ToTable("invoices");
            e.Property(i => i.Id).ValueGeneratedNever();
            // A uint row version maps to xmin. EF Core adds "AND xmin = @original" to every UPDATE and DELETE
            // of this entity, and throws DbUpdateConcurrencyException when no row matches.
            e.Property(i => i.Version).IsRowVersion();
        });
    }
}
