using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace StatusCodePolicy;

public sealed record Signup(string Email);

public sealed class User
{
    public int Id { get; set; }
    public required string Email { get; set; }
}

public static class Email
{
    // One place decides what "the same email" means, before it reaches the database.
    public static string Normalize(string email) => email.Trim().ToLowerInvariant();
}

// Shape only: everything here can be judged from the request and the API contract alone.
public sealed class SignupValidator : AbstractValidator<Signup>
{
    public SignupValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(254);
    }
}

public sealed class AppDb(DbContextOptions<AppDb> options) : DbContext(options)
{
    public const string UsersEmailIndex = "ix_users_email";

    public DbSet<User> Users => Set<User>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>().ToTable("users")
            .HasIndex(u => u.Email)
            .IsUnique()
            .HasDatabaseName(UsersEmailIndex);
    }
}
