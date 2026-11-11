using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using StatusCodePolicy;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDbContext<AppDb>(o => o.UseNpgsql(builder.Configuration.GetConnectionString("Db")));
builder.Services.AddScoped<SignupValidator>();
builder.Services.AddProblemDetails();

// Domains whose accounts are created another way (for example by the company's own sign-in).
var closedDomains = builder.Configuration.GetSection("Signup:ClosedDomains").Get<string[]>() ?? [];

var app = builder.Build();

// Failures the endpoint doesn't produce itself (broken JSON, unknown routes, bugs) get a Problem Details
// body too, as long as the client accepts JSON. A client that asks only for text/html gets plain text.
app.UseExceptionHandler(new ExceptionHandlerOptions
{
    // In Development, minimal APIs throw BadHttpRequestException for a body they cannot bind
    // (in Production they just answer 400). Without this, the handler would turn it into a 500.
    StatusCodeSelector = ex => ex is BadHttpRequestException bad ? bad.StatusCode : StatusCodes.Status500InternalServerError,
});
app.UseStatusCodePages();

// A fresh schema on every start keeps the demo repeatable. Real projects use migrations.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDb>();
    await db.Database.EnsureDeletedAsync();
    await db.Database.EnsureCreatedAsync();
}

// The endpoint never picks a status code. It picks an error from the catalogue in Errors.cs.
app.MapPost("/users", async (Signup signup, SignupValidator validator, AppDb db, CancellationToken ct) =>
{
    // Question 1: does the API contract say this request is wrong? Then 400.
    var result = await validator.ValidateAsync(signup, ct);
    if (!result.IsValid)
    {
        return Errors.Validation(result.ToDictionary());
    }

    // The three questions classify a failure; they don't fix the order of the checks.
    // When a request could fail two ways, the API needs a precedence, and this one checks the
    // business rule (question 3) before the insert (question 2): it needs no database round trip,
    // and a closed domain is refused whether or not the address is already registered.
    // Is the request fine, but a business rule refuses it? Then 422.
    var email = Email.Normalize(signup.Email);
    var domain = Email.DomainOf(email);
    if (closedDomains.Contains(domain))
    {
        return Errors.SignupDomainClosed.ToProblem($"Accounts for {domain} are created by the organisation's administrator.");
    }

    // Question 2: is the request fine, but the current state rules it out? Then 409.
    // The unique index decides; the catch translates its answer.
    var user = new User { Email = email };
    db.Users.Add(user);
    try
    {
        await db.SaveChangesAsync(ct);
    }
    catch (DbUpdateException ex) when (ex.InnerException is PostgresException
    {
        SqlState: PostgresErrorCodes.UniqueViolation,
        ConstraintName: AppDb.UsersEmailIndex
    })
    {
        return Errors.EmailTaken.ToProblem("An account with this email already exists.");
    }

    return Results.Created($"/users/{user.Id}", new { user.Id, user.Email });
});

app.Run();
