using System.Net.Http.Json;

namespace HandlersWithDecorators;

// Sends the same two bad orders to all three endpoints and prints what each one answered and left behind.
// It also checks the answers: anything unexpected is reported and the process exits with code 1.
public static class Demo
{
    // InvalidStatus is the expected answer to the invalid order. Only the unprotected endpoint accepts it.
    private static readonly (string Label, string Path, int InvalidStatus)[] Endpoints =
    [
        ("MediatR with behaviors", "/mediatr/orders", 400),
        ("Handler called directly", "/plain/orders", 201),
        ("Handler with decorators", "/orders", 400),
    ];

    // What a fresh database holds after the two orders: orders saved and widgets in stock.
    private static readonly Dictionary<string, (int Orders, int Widgets)> ExpectedStats = new()
    {
        [Sources.MediatR] = (0, 10),
        [Sources.Plain] = (2, 14),
        [Sources.Decorated] = (0, 10),
    };

    public static async Task RunAsync(string baseUrl)
    {
        using var http = new HttpClient { BaseAddress = new Uri(baseUrl) };
        await WaitForApiAsync(http);
        var unexpected = new List<string>();

        Console.WriteLine("1. An order for -5 widgets (the request is invalid)");
        var minusFive = new PlaceOrderBody("customer-1", [new OrderLineRequest("widget", -5)]);
        foreach (var (label, path, invalidStatus) in Endpoints)
        {
            var status = await PostAsync(http, path, minusFive);
            Console.WriteLine($"   {label,-24} POST {path,-15} {status}");
            if (status != invalidStatus) unexpected.Add($"{path} answered {status} to the invalid order, expected {invalidStatus}");
        }

        Console.WriteLine();
        Console.WriteLine("2. An order for 1 widget and 1 gadget (gadgets are out of stock)");
        var outOfStock = new PlaceOrderBody("customer-1", [new OrderLineRequest("widget", 1), new OrderLineRequest("gadget", 1)]);
        foreach (var (label, path, _) in Endpoints)
        {
            var status = await PostAsync(http, path, outOfStock);
            Console.WriteLine($"   {label,-24} POST {path,-15} {status}");
            if (status != 409) unexpected.Add($"{path} answered {status} to the out-of-stock order, expected 409");
        }

        Console.WriteLine();
        Console.WriteLine("What each endpoint left behind (GET /stats). Each started with 10 widgets and no orders:");
        foreach (var row in (await http.GetFromJsonAsync<List<StatsRow>>("/stats"))!)
        {
            Console.WriteLine($"   {row.Source,-10} {row.Orders} order(s) saved, {row.WidgetsInStock} widgets in stock");
            var (orders, widgets) = ExpectedStats[row.Source];
            if (row.Orders != orders || row.WidgetsInStock != widgets)
            {
                unexpected.Add($"{row.Source} has {row.Orders} order(s) and {row.WidgetsInStock} widgets, expected {orders} and {widgets}");
            }
        }

        if (unexpected.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("UNEXPECTED. If the demo already ran against this API, restart the API to reset the database.");
            foreach (var line in unexpected)
            {
                Console.WriteLine($"   {line}");
            }

            Environment.ExitCode = 1;
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
