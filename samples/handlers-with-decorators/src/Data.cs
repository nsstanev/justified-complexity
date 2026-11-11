using Microsoft.EntityFrameworkCore;

namespace HandlersWithDecorators;

// The three endpoints share one database. Every row carries the endpoint that wrote it,
// so GET /stats can show what each one left behind.
public static class Sources
{
    public const string MediatR = "mediatr";
    public const string Plain = "plain";
    public const string Decorated = "decorated";

    public static readonly string[] All = [MediatR, Plain, Decorated];
}

public sealed class Order
{
    public Guid Id { get; set; }
    public required string Source { get; set; }
    public required string CustomerId { get; set; }
    public List<OrderLine> Lines { get; set; } = [];
}

public sealed class OrderLine
{
    public int Id { get; set; }
    public Guid OrderId { get; set; }
    public required string Sku { get; set; }
    public int Quantity { get; set; }
}

public sealed class StockItem
{
    public required string Source { get; set; }
    public required string Sku { get; set; }
    public int Available { get; set; }
}

public sealed class AppDb(DbContextOptions<AppDb> options) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<StockItem> Stock => Set<StockItem>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Order>(e =>
        {
            e.ToTable("orders");
            e.HasMany(o => o.Lines).WithOne().HasForeignKey(l => l.OrderId);
        });

        b.Entity<OrderLine>().ToTable("order_lines");

        b.Entity<StockItem>(e =>
        {
            e.ToTable("stock");
            e.HasKey(s => new { s.Source, s.Sku });
            e.Property(s => s.Source).HasColumnName("source");
            e.Property(s => s.Sku).HasColumnName("sku");
            e.Property(s => s.Available).HasColumnName("available");
        });
    }

    // Every endpoint starts with the same shelf: ten widgets and no gadgets.
    public async Task SeedAsync()
    {
        foreach (var source in Sources.All)
        {
            Stock.Add(new StockItem { Source = source, Sku = "widget", Available = 10 });
            Stock.Add(new StockItem { Source = source, Sku = "gadget", Available = 0 });
        }

        await SaveChangesAsync();
    }
}
