using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace ConstraintDemo;

// Shape only: depends on the request alone, so there is no race.
public sealed class SignupShapeValidator : AbstractValidator<Signup>
{
    public SignupShapeValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(254);
    }
}

// The version everyone writes first: shape plus an "is it taken?" lookup.
public sealed class NaiveSignupValidator : AbstractValidator<Signup>
{
    public const string TakenCode = "EmailTaken";

    public NaiveSignupValidator(AppDb db)
    {
        // The lookup is a dependent rule: it runs only when the shape rules pass,
        // so a missing or malformed email never reaches the database.
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(254)
            .DependentRules(() =>
            {
                RuleFor(x => x.Email)
                    .MustAsync(async (email, ct) =>
                    {
                        var normalized = Email.Normalize(email);
                        return !await db.NaiveUsers.AnyAsync(u => u.Email == normalized, ct);
                    })
                    .WithErrorCode(TakenCode)
                    .WithMessage("This email is already registered.");
            });
    }
}
