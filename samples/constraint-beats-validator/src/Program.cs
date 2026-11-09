using ConstraintDemo;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Npgsql;

// "dotnet run -- race" runs the race client against an API that is already running.
if (args is ["race", ..])
{
    await Race.RunAsync(args.Length > 1 ? args[1] : "http://localhost:5080");
    return;
}

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDbContext<AppDb>(o => o.UseNpgsql(builder.Configuration.GetConnectionString("Db")));
builder.Services.AddScoped<NaiveSignupValidator>();
builder.Services.AddScoped<SignupShapeValidator>();
builder.Services.AddProblemDetails();

var app = builder.Build();

// A fresh schema on every start keeps the demo repeatable. Real projects use migrations.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDb>();
    await db.Database.EnsureDeletedAsync();
    await db.Database.EnsureCreatedAsync();
}

// Option 1: the async validator is the only guard. naive_users has no unique index.
app.MapPost("/naive/users", async (Signup signup, int? delayMs, NaiveSignupValidator validator, AppDb db, CancellationToken ct) =>
{
    var result = await validator.ValidateAsync(signup, ct);
    if (!result.IsValid)
    {
        return result.Errors.Any(e => e.ErrorCode == NaiveSignupValidator.TakenCode)
            ? Results.Problem(title: "Email already registered", statusCode: StatusCodes.Status409Conflict)
            : Results.ValidationProblem(result.ToDictionary());
    }

    await Task.Delay(delayMs ?? 0, ct); // stands in for real work between the check and the insert

    var user = new NaiveUser { Email = Email.Normalize(signup.Email) };
    db.NaiveUsers.Add(user);
    await db.SaveChangesAsync(ct);
    return Results.Created($"/naive/users/{user.Id}", new { user.Id, user.Email });
});

// Option 2: validate shape in code, let the unique index decide, translate the violation into a 409.
app.MapPost("/users", async (Signup signup, int? delayMs, SignupShapeValidator validator, AppDb db, CancellationToken ct) =>
{
    var result = await validator.ValidateAsync(signup, ct);
    if (!result.IsValid)
    {
        return Results.ValidationProblem(result.ToDictionary());
    }

    await Task.Delay(delayMs ?? 0, ct); // same artificial delay as option 1, to keep the comparison fair

    var user = new User { Email = Email.Normalize(signup.Email) };
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
        return Results.Problem(
            title: "Email already registered",
            detail: "An account with this email already exists.",
            statusCode: StatusCodes.Status409Conflict);
    }

    return Results.Created($"/users/{user.Id}", new { user.Id, user.Email });
});

// How many rows each table holds per email: the duplicates are the point of the demo.
app.MapGet("/stats", async (AppDb db, CancellationToken ct) => new
{
    Naive = await db.NaiveUsers.GroupBy(u => u.Email).Select(g => new { Email = g.Key, Rows = g.Count() }).ToListAsync(ct),
    Constrained = await db.Users.GroupBy(u => u.Email).Select(g => new { Email = g.Key, Rows = g.Count() }).ToListAsync(ct),
});

app.Run();
