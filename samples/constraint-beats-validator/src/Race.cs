using System.Net.Http.Json;

namespace ConstraintDemo;

// Fires the same signup many times at once against both endpoints and prints what happened.
public static class Race
{
    private const int Requests = 20;
    private const int DelayMs = 30; // widens the gap between check and insert so the race shows up reliably

    public static async Task RunAsync(string baseUrl)
    {
        using var http = new HttpClient { BaseAddress = new Uri(baseUrl) };

        await WaitForApiAsync(http);
        // A fresh address for every run, shared by both endpoints, so the client can be rerun
        // against a running API. With a fixed address the second run would only see conflicts.
        var email = $"dana+{Guid.NewGuid().ToString("N")[..8]}@acme.example";
        await RunOneAsync(http, "/naive/users", email);
        await RunOneAsync(http, "/users", email);

        Console.WriteLine();
        Console.WriteLine("Rows per email (GET /stats):");
        Console.WriteLine(await http.GetStringAsync("/stats"));
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

    private static async Task RunOneAsync(HttpClient http, string path, string email)
    {
        using var start = new SemaphoreSlim(0);
        var tasks = Enumerable.Range(0, Requests).Select(async _ =>
        {
            await start.WaitAsync();
            using var response = await http.PostAsJsonAsync($"{path}?delayMs={DelayMs}", new Signup(email));
            return (int)response.StatusCode;
        }).ToList();

        start.Release(Requests); // let every request go at the same moment
        var codes = await Task.WhenAll(tasks);

        var summary = string.Join(", ", codes.GroupBy(c => c).OrderBy(g => g.Key).Select(g => $"{g.Count()} x {g.Key}"));
        Console.WriteLine($"POST {path,-13} {Requests} concurrent signups for {email}: {summary}");
    }
}
