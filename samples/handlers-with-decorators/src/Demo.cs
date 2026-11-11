using System.Net.Http.Json;

namespace HandlersWithDecorators;

// Sends the same two bad orders to all three endpoints and prints what each one answered and left behind.
public static class Demo
{
    private static readonly (string Label, string Path)[] Endpoints =
    [
        ("MediatR with behaviors", "/mediatr/orders"),
        ("Handler called directly", "/plain/orders"),
        ("Handler with decorators", "/orders"),
    ];

    public static async Task RunAsync(string baseUrl)
    {
        using var http = new HttpClient { BaseAddress = new Uri(baseUrl) };
        await WaitForApiAsync(http);

        Console.WriteLine("1. An order for -5 widgets (the request is invalid)");
        var minusFive = new PlaceOrderBody("customer-1", [new OrderLineRequest("widget", -5)]);
        foreach (var (label, path) in Endpoints)
        {
            Console.WriteLine($"   {label,-24} POST {path,-15} {await PostAsync(http, path, minusFive)}");
        }

        Console.WriteLine();
        Console.WriteLine("2. An order for 1 widget and 1 gadget (gadgets are out of stock)");
        var outOfStock = new PlaceOrderBody("customer-1", [new OrderLineRequest("widget", 1), new OrderLineRequest("gadget", 1)]);
        foreach (var (label, path) in Endpoints)
        {
            Console.WriteLine($"   {label,-24} POST {path,-15} {await PostAsync(http, path, outOfStock)}");
        }

        Console.WriteLine();
        Console.WriteLine("What each endpoint left behind (GET /stats). Each started with 10 widgets and no orders:");
        foreach (var row in (await http.GetFromJsonAsync<List<StatsRow>>("/stats"))!)
        {
            Console.WriteLine($"   {row.Source,-10} {row.Orders} order(s) saved, {row.WidgetsInStock} widgets in stock");
        }
    }

    private sealed record StatsRow(string Source, int Orders, int WidgetsInStock);

    private static async Task<int> PostAsync(HttpClient http, string path, PlaceOrderBody body)
    {
        using var response = await http.PostAsJsonAsync(path, body);
        return (int)response.StatusCode;
    }

    // The API recreates its schema on start, so give it a moment when both start together in Docker.
    private static async Task WaitForApiAsync(HttpClient http)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var response = await http.GetAsync("/stats");
                if (response.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException) when (attempt < 30)
            {
            }

            if (attempt >= 30) throw new InvalidOperationException($"API at {http.BaseAddress} did not become ready.");
            await Task.Delay(1000);
        }
    }
}
