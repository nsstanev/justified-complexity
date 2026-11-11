namespace StatusCodePolicy;

// The whole status code policy: three kinds of failure, three codes.
public enum ErrorKind
{
    Invalid = 400,  // wrong according to the API contract: fix the request
    Conflict = 409, // fine on its own, but the resource's current state says no
    Refused = 422,  // fine on its own, but a business rule says no
}

// One entry in the error catalogue. Code is the stable part clients may branch on.
public sealed record Error(string Code, ErrorKind Kind, string Title)
{
    private const string BaseUri = "https://errors.acme.example/";

    public string Type => BaseUri + Code;

    public IResult ToProblem(string? detail = null) =>
        Results.Problem(type: Type, title: Title, detail: detail, statusCode: (int)Kind);
}

public static class Errors
{
    public static readonly Error ValidationFailed =
        new("validation-failed", ErrorKind.Invalid, "One or more fields are invalid");

    public static readonly Error EmailTaken =
        new("email-taken", ErrorKind.Conflict, "Email already registered");

    public static readonly Error SignupDomainClosed =
        new("signup-domain-closed", ErrorKind.Refused, "Signups are closed for this domain");

    // Field errors keep the "errors" member that ASP.NET Core clients already know.
    public static IResult Validation(IDictionary<string, string[]> errors) =>
        Results.ValidationProblem(
            errors,
            type: ValidationFailed.Type,
            title: ValidationFailed.Title,
            statusCode: (int)ValidationFailed.Kind);
}
