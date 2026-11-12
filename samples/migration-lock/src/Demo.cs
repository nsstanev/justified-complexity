using System.Diagnostics;
using System.Net.Http.Json;

namespace MigrationLock;

// Sends steady traffic to the API while each schema change runs, and prints how many requests failed.
// It also checks the result: anything unexpected is reported and the process exits with code 1.
public static class Demo
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(1);
    private const int SlowMs = 500;

    public static async Task RunAsync(string baseUrl)
    {
        using var http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromMinutes(5) };
        var rows = await WaitForApiAsync(http);
        var unexpected = new List<string>();
        Console.WriteLine($"Table invoices: {rows:N0} rows. Traffic: 5 reads and 5 writes per second, each with a 1 s timeout.");

        Console.WriteLine();
        Console.WriteLine("1. ALTER TABLE ... ADD COLUMN while a 5-second report is reading the table");
        await http.PostAsync("/admin/report?seconds=5", null);
        await Task.Delay(300);
        var (migration, reads, writes) = await RunUnderTrafficAsync(http, "add-column");
        Print(migration, reads, writes);
        if (reads.Failed + writes.Failed < 10) unexpected.Add("add-column: expected the requests behind the ALTER TABLE to time out");

        await Task.Delay(1500); // let the requests that were queued finish
        Console.WriteLine();
        Console.WriteLine("2. The same, with lock_timeout = 300 ms and a retry");
        await http.PostAsync("/admin/report?seconds=5", null);
        await Task.Delay(300);
        (migration, reads, writes) = await RunUnderTrafficAsync(http, "add-column-safe");
        Print(migration, reads, writes);
        if (reads.Failed + writes.Failed > 0) unexpected.Add("add-column-safe: expected no failed requests");
        if (migration.Attempts < 2) unexpected.Add("add-column-safe: expected more than one attempt");

        Console.WriteLine();
        Console.WriteLine("3. CREATE INDEX");
        (migration, reads, writes) = await RunUnderTrafficAsync(http, "index");
        Print(migration, reads, writes);
        if (reads.Failed > 0) unexpected.Add("index: expected reads to keep working");
        if (writes.Failed + writes.Slow == 0) unexpected.Add("index: expected writes to wait for the index build");

        await Task.Delay(1500);
        Console.WriteLine();
        Console.WriteLine("4. CREATE INDEX CONCURRENTLY");
        (migration, reads, writes) = await RunUnderTrafficAsync(http, "index-concurrently");
        Print(migration, reads, writes);
        if (reads.Failed + writes.Failed > 0) unexpected.Add("index-concurrently: expected no failed requests");

        await Task.Delay(1500);
        Console.WriteLine();
        Console.WriteLine("5. ADD CONSTRAINT ... CHECK");
        (migration, reads, writes) = await RunUnderTrafficAsync(http, "check");
        Print(migration, reads, writes);
        if (reads.Failed + reads.Slow + writes.Failed + writes.Slow == 0) unexpected.Add("check: expected requests to wait while every row is checked");

        await Task.Delay(1500);
        Console.WriteLine();
        Console.WriteLine("6. ADD CONSTRAINT ... CHECK NOT VALID, then VALIDATE CONSTRAINT");
        (migration, reads, writes) = await RunUnderTrafficAsync(http, "check-not-valid");
        Print(migration, reads, writes);
        if (reads.Failed + writes.Failed > 0) unexpected.Add("check-not-valid: expected no failed requests");

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

    private sealed record Traffic(int Sent, int Failed, int Slow, long SlowestMs);

    private static void Print(MigrationResult migration, Traffic reads, Traffic writes)
    {
        Console.WriteLine($"   migration: {migration.Attempts} attempt(s), {migration.ElapsedMs / 1000.0:0.0} s");
        Console.WriteLine($"   reads:     {reads.Sent} sent, {reads.Failed} timed out, {reads.Slow} slower than {SlowMs} ms");
        Console.WriteLine($"   writes:    {writes.Sent} sent, {writes.Failed} timed out, {writes.Slow} slower than {SlowMs} ms");
    }

    // Runs the migration and, until half a second after it returns, sends one read and one write every 200 ms.
    private static async Task<(MigrationResult Migration, Traffic Reads, Traffic Writes)> RunUnderTrafficAsync(HttpClient http, string name)
    {
        var reads = new List<Task<long>>();
        var writes = new List<Task<long>>();
        using var stop = new CancellationTokenSource();

        var traffic = Task.Run(async () =>
        {
            var random = new Random(42);
            while (!stop.IsCancellationRequested)
            {
                var id = random.Next(1, 100_000);
                reads.Add(TimeAsync(http, HttpMethod.Get, $"/invoices/{id}"));
                writes.Add(TimeAsync(http, HttpMethod.Post, $"/invoices/{id}/pay"));
                await Task.Delay(200);
            }
        });

        using var response = await http.PostAsync($"/admin/migrations/{name}", null);
        response.EnsureSuccessStatusCode();
        var migration = (await response.Content.ReadFromJsonAsync<MigrationResult>())!;

        await Task.Delay(500);
        stop.Cancel();
        await traffic;
        return (migration, Summarise(await Task.WhenAll(reads)), Summarise(await Task.WhenAll(writes)));
    }

    // The latency in milliseconds, or -1 when the request timed out or failed.
    private static async Task<long> TimeAsync(HttpClient http, HttpMethod method, string path)
    {
        var watch = Stopwatch.StartNew();
        using var timeout = new CancellationTokenSource(RequestTimeout);
        try
        {
            using var response = await http.SendAsync(new HttpRequestMessage(method, path), timeout.Token);
            return response.IsSuccessStatusCode ? watch.ElapsedMilliseconds : -1;
        }
        catch (OperationCanceledException)
        {
            return -1;
        }
    }

    private static Traffic Summarise(long[] latencies) => new(
        latencies.Length,
        latencies.Count(l => l < 0),
        latencies.Count(l => l >= SlowMs),
        latencies.Length == 0 ? 0 : latencies.Max());

    // The API loads the table on start, which takes a while.
    private static async Task<int> WaitForApiAsync(HttpClient http)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var response = await http.GetAsync("/health");
                if (response.IsSuccessStatusCode) return (await response.Content.ReadFromJsonAsync<Health>())!.Rows;
            }
            catch (HttpRequestException) when (attempt < 120)
            {
            }

            if (attempt >= 120) throw new InvalidOperationException($"API at {http.BaseAddress} did not become ready.");
            await Task.Delay(1000);
        }
    }

    private sealed record Health(int Rows);
}
