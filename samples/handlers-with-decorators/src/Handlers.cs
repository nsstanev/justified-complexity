using System.Diagnostics;
using FluentValidation;

namespace HandlersWithDecorators;

// The whole "framework": one interface. An endpoint asks for the handler it needs by type.
public interface IHandler<in TRequest, TResponse>
{
    Task<TResponse> HandleAsync(TRequest request, CancellationToken ct);
}

// A decorator is a handler that wraps another handler. Each one does a single job and calls the next.

public sealed class LoggingDecorator<TRequest, TResponse>(
    IHandler<TRequest, TResponse> inner,
    ILogger<LoggingDecorator<TRequest, TResponse>> logger) : IHandler<TRequest, TResponse>
{
    public async Task<TResponse> HandleAsync(TRequest request, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            var response = await inner.HandleAsync(request, ct);
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

public sealed class ValidationDecorator<TRequest, TResponse>(
    IHandler<TRequest, TResponse> inner,
    IEnumerable<IValidator<TRequest>> validators) : IHandler<TRequest, TResponse>
{
    public async Task<TResponse> HandleAsync(TRequest request, CancellationToken ct)
    {
        foreach (var validator in validators)
        {
            await validator.ValidateAndThrowAsync(request, ct); // ValidationException becomes a 400 in Program.cs
        }

        return await inner.HandleAsync(request, ct);
    }
}

public sealed class TransactionDecorator<TRequest, TResponse>(
    IHandler<TRequest, TResponse> inner,
    AppDb db) : IHandler<TRequest, TResponse>
{
    public async Task<TResponse> HandleAsync(TRequest request, CancellationToken ct)
    {
        // Everything the handler writes commits together. If it throws, disposing the transaction rolls it back.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var response = await inner.HandleAsync(request, ct);
        await transaction.CommitAsync(ct);
        return response;
    }
}

public static class HandlerRegistration
{
    // One line per use case in Program.cs. The order is written out here, outermost first:
    // logging sees everything, validation runs before a transaction is opened.
    public static IServiceCollection AddHandler<TRequest, TResponse, THandler>(this IServiceCollection services)
        where THandler : class, IHandler<TRequest, TResponse>
    {
        services.AddScoped<THandler>();
        services.AddScoped<IHandler<TRequest, TResponse>>(sp =>
            new LoggingDecorator<TRequest, TResponse>(
                new ValidationDecorator<TRequest, TResponse>(
                    new TransactionDecorator<TRequest, TResponse>(
                        sp.GetRequiredService<THandler>(),
                        sp.GetRequiredService<AppDb>()),
                    sp.GetServices<IValidator<TRequest>>()),
                sp.GetRequiredService<ILogger<LoggingDecorator<TRequest, TResponse>>>()));

        return services;
    }
}
