using System.Diagnostics;
using FluentValidation;
using MediatR;

namespace HandlersWithDecorators;

// The MediatR version of the same three jobs. Compare each one with its decorator in Handlers.cs:
// the body is the same, only the plumbing around it differs.

public sealed class LoggingBehavior<TRequest, TResponse>(ILogger<LoggingBehavior<TRequest, TResponse>> logger)
    : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            var response = await next();
            logger.LogInformation("{Request} handled in {Elapsed:0} ms", typeof(TRequest).Name, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return response;
        }
        catch (Exception ex)
        {
            logger.LogWarning("{Request} failed after {Elapsed:0} ms: {Error}", typeof(TRequest).Name, Stopwatch.GetElapsedTime(started).TotalMilliseconds, ex.Message);
            throw;
        }
    }
}

public sealed class ValidationBehavior<TRequest, TResponse>(IEnumerable<IValidator<TRequest>> validators)
    : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken ct)
    {
        foreach (var validator in validators)
        {
            await validator.ValidateAndThrowAsync(request, ct);
        }

        return await next();
    }
}

public sealed class TransactionBehavior<TRequest, TResponse>(AppDb db)
    : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var response = await next();
        await transaction.CommitAsync(ct);
        return response;
    }
}

// MediatR finds this by scanning the assembly. It runs the same business logic as the other two endpoints.
public sealed class PlaceOrderMediatRHandler(PlaceOrderHandler inner) : IRequestHandler<PlaceOrder, OrderPlaced>
{
    public Task<OrderPlaced> Handle(PlaceOrder request, CancellationToken ct) => inner.HandleAsync(request, ct);
}
