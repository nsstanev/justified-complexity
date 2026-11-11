using System.Net.Http.Json;

namespace DuplicateCharge;

// Plays a client with a flaky connection against both endpoints and prints what happened.
public static class Demo
{
    private const int Retries = 5;
    private const int DelayMs = 300; // how long the fake provider takes; gives the retries time to overlap

    public static async Task RunAsync(string baseUrl)
    {
        using var http = new HttpClient { BaseAddress = new Uri(baseUrl) };
        await WaitForApiAsync(http);

        Console.WriteLine("1. No key: the client retries a payment of 49.90 five times at once");
        var codes = await Task.WhenAll(Enumerable.Range(0, Retries).Select(_ => PayAsync(http, "/naive/payments", "order-1001", 49.90m, key: null)));
        Console.WriteLine($"   POST /naive/payments  {Summary(codes)}");

        Console.WriteLine();
        Console.WriteLine("2. Same retries, with one Idempotency-Key");
        var key = Guid.NewGuid().ToString();
        codes = await Task.WhenAll(Enumerable.Range(0, Retries).Select(_ => PayAsync(http, "/payments", "order-1002", 49.90m, key)));
        Console.WriteLine($"   POST /payments        {Summary(codes)}");
        Console.WriteLine($"   retry after it finished: {await PayAsync(http, "/payments", "order-1002", 49.90m, key)}");

        Console.WriteLine();
        Console.WriteLine("3. The same key with a different amount");
        Console.WriteLine($"   POST /payments        {await PayAsync(http, "/payments", "order-1002", 499.00m, key)}");

        Console.WriteLine();
        Console.WriteLine("4. The API crashes after the card is charged, before the payment is saved");
        key = Guid.NewGuid().ToString();
        Console.WriteLine($"   first attempt:        {await PayAsync(http, "/payments", "order-1003", 49.90m, key, crash: true)}");
        Console.WriteLine($"   immediate retry:      {await PayAsync(http, "/payments", "order-1003", 49.90m, key)}");
        await Task.Delay(2500); // the demo's lock timeout is 2 seconds
        Console.WriteLine($"   retry after 2.5 s:    {await PayAsync(http, "/payments", "order-1003", 49.90m, key)}");

        Console.WriteLine();
        Console.WriteLine("What the API saved and what the provider charged (GET /stats):");
        foreach (var row in (await http.GetFromJsonAsync<List<StatsRow>>("/stats"))!)
        {
            Console.WriteLine($"   {row.OrderId}: {row.PaymentRows} payment row(s), {row.ProviderCharges} charge(s), {row.TotalCharged:0.00} charged");
        }
    }

    private sealed record StatsRow(string OrderId, int PaymentRows, int ProviderCharges, decimal TotalCharged);

    private static async Task<string> PayAsync(HttpClient http, string path, string orderId, decimal amount, string? key, bool crash = false)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, $"{path}?delayMs={DelayMs}{(crash ? "&crashAfterCharge=true" : "")}")
        {
            Content = JsonContent.Create(new PaymentRequest(orderId, amount)),
        };
        if (key is not null)
        {
            message.Headers.Add("Idempotency-Key", key);
        }

        using var response = await http.SendAsync(message);
        return response.Headers.Contains("Idempotent-Replayed")
            ? $"{(int)response.StatusCode} (replayed)"
            : $"{(int)response.StatusCode}";
    }

    private static string Summary(IEnumerable<string> codes) =>
        string.Join(", ", codes.GroupBy(c => c).OrderBy(g => g.Key).Select(g => $"{g.Count()} x {g.Key}"));

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
