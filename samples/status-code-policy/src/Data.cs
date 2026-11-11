using System.Text.RegularExpressions;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace StatusCodePolicy;

public sealed record Signup(string Email);

public sealed class User
{
    public int Id { get; set; }
    public required string Email { get; set; }
}

public static partial class Email
{
    // This application's policy, not a rule of email: addresses are case-insensitive account
    // identifiers. One place trims surrounding whitespace and lower-cases the value that is
    // stored and compared. Keep the address as entered in a second column if you deliver to it.
    public static string Normalize(string email) => email.Trim().ToLowerInvariant();

    // The syntax this API accepts: one @, no whitespace, a dot in the domain. Stricter than
    // FluentValidation's EmailAddress(), which only looks for an @ and would let "dana@ corp.example"
    // through to the domain rule. Still not proof that the mailbox exists; only a sent email is.
    public static bool HasAcceptedSyntax(string? email) =>
        email is not null && AcceptedSyntax().IsMatch(email.Trim());

    // Call only after HasAcceptedSyntax and Normalize.
    public static string DomainOf(string normalizedEmail) => normalizedEmail[(normalizedEmail.IndexOf('@') + 1)..];

    [GeneratedRegex(@"^[^@\s]+@[^@\s.]+(\.[^@\s.]+)+$")]
    private static partial Regex AcceptedSyntax();
}

// Shape only: everything here can be judged from the request and the API contract alone.
public sealed class SignupValidator : AbstractValidator<Signup>
{
    public SignupValidator()
    {
        RuleFor(x => x.Email)
            .Cascade(CascadeMode.Stop) // one message per mistake
            .NotEmpty()
            .MaximumLength(254)
            .Must(Email.HasAcceptedSyntax).WithMessage("'Email' must look like name@example.com, with no spaces.");
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
