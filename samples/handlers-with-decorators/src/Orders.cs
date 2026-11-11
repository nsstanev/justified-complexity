using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace HandlersWithDecorators;

// The HTTP body.
public sealed record OrderLineRequest(string Sku, int Quantity);
public sealed record PlaceOrderBody(string CustomerId, List<OrderLineRequest>? Lines);

// The use case. It implements MediatR's IRequest only so the same record can travel through all three endpoints.
public sealed record PlaceOrder(string Source, string CustomerId, IReadOnlyList<OrderLineRequest> Lines) : IRequest<OrderPlaced>;
public sealed record OrderPlaced(Guid OrderId);

public sealed class OutOfStockException(string sku) : Exception($"Not enough stock for '{sku}'.")
{
    public string Sku => sku;
}

// Shape only: everything here can be judged from the request alone.
public sealed class PlaceOrderValidator : AbstractValidator<PlaceOrder>
{
    public PlaceOrderValidator()
    {
        RuleFor(x => x.CustomerId).NotEmpty();
        RuleFor(x => x.Lines).NotEmpty();
        RuleForEach(x => x.Lines).ChildRules(line =>
        {
            line.RuleFor(l => l.Sku).NotEmpty();
            line.RuleFor(l => l.Quantity).GreaterThan(0);
        });
    }
}

// The business logic, written once. It does two writes and knows nothing about
// logging, validation or transactions: those wrap it from the outside, or don't.
public sealed class PlaceOrderHandler(AppDb db) : IHandler<PlaceOrder, OrderPlaced>
{
    public async Task<OrderPlaced> HandleAsync(PlaceOrder request, CancellationToken ct)
    {
        // First write: the order and its lines.
        var order = new Order
        {
            Id = Guid.CreateVersion7(),
            Source = request.Source,
            CustomerId = request.CustomerId,
            Lines = request.Lines.Select(l => new OrderLine { Sku = l.Sku, Quantity = l.Quantity }).ToList(),
        };
        db.Orders.Add(order);
        await db.SaveChangesAsync(ct);

        // Second write: reserve the stock, line by line. The WHERE clause refuses to go below zero.
        foreach (var line in request.Lines)
        {
            var reserved = await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE stock SET available = available - {line.Quantity}
                WHERE source = {request.Source} AND sku = {line.Sku} AND available >= {line.Quantity}
                """, ct);

            if (reserved == 0)
            {
                throw new OutOfStockException(line.Sku);
            }
        }

        return new OrderPlaced(order.Id);
    }
}
