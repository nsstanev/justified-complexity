using Microsoft.EntityFrameworkCore;

namespace ConstraintDemo;

public sealed record Signup(string Email);

public sealed class NaiveUser
{
    public int Id { get; set; }
    public required string Email { get; set; }
}

public sealed class User
{
    public int Id { get; set; }
    public required string Email { get; set; }
}

public static class Email
{
    // This application's policy, not a rule of email: addresses are case-insensitive account
    // identifiers. One place trims surrounding whitespace and lower-cases the value that is
    // stored and compared. Keep the address as entered in a second column if you deliver to it.
    public static string Normalize(string email) => email.Trim().ToLowerInvariant();
}

public sealed class AppDb(DbContextOptions<AppDb> options) : DbContext(options)
{
    public const string UsersEmailIndex = "ix_users_email";

    public DbSet<NaiveUser> NaiveUsers => Set<NaiveUser>();
    public DbSet<User> Users => Set<User>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<NaiveUser>().ToTable("naive_users");

        b.Entity<User>().ToTable("users")
            .HasIndex(u => u.Email)
            .IsUnique()
            .HasDatabaseName(UsersEmailIndex);
    }
}
