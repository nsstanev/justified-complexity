using System.Net.Http.Json;

namespace LostUpdate;

// Edits the same invoice from several clients at once and prints what survived.
// It also checks the result: anything unexpected is reported and the process exits with code 1.
public static class Demo
{
    private const int Editors = 20;
    private static readonly DateOnly NewDueDate = new(2026, 10, 15);

    public static async Task RunAsync(string baseUrl)
    {
        using var http = new HttpClient { BaseAddress = new Uri(baseUrl) };
        await WaitForApiAsync(http);
        var unexpected = new List<string>();

        Console.WriteLine("1. Alice and Bob open the same invoice. Alice changes the due date, then Bob adds a PO number.");
        foreach (var (label, path, id, bobExpected) in new[] { ("no version check", "/last-write-wins/invoices", 1, 204), ("If-Match", "/invoices", 2, 412) })
        {
            var (opened, etag) = await GetAsync(http, id);
            var alice = await PutAsync(http, $"{path}/{id}", new InvoiceUpdate(opened.Customer, opened.PoNumber, NewDueDate, opened.Total), etag);
            var bob = await PutAsync(http, $"{path}/{id}", new InvoiceUpdate(opened.Customer, "PO-7781", opened.DueDate, opened.Total), etag);
            var (saved, _) = await GetAsync(http, id);

            var note = saved.DueDate == NewDueDate ? "Bob is told to reload" : "Alice's change is gone";
            Console.WriteLine($"   {label + ":",-18} Alice {alice}, Bob {bob}. Saved: due {saved.DueDate:yyyy-MM-dd}, PO {saved.PoNumber ?? "none"}   <- {note}");
            if (bob != bobExpected) unexpected.Add($"{path}: Bob got {bob}, expected {bobExpected}");
            if ((saved.DueDate == NewDueDate) != (bobExpected == 412)) unexpected.Add($"{path}: unexpected due date {saved.DueDate}");
        }

        Console.WriteLine();
        Console.WriteLine($"2. {Editors} people each add 10.00 to an invoice of 100.00 at the same moment. The total should be 300.00.");

        // Everyone reads first, then everyone saves: the same stale copy on {Editors} screens.
        var (stale, _) = await GetAsync(http, 3);
        var codes = await Task.WhenAll(Enumerable.Range(0, Editors).Select(_ =>
            PutAsync(http, "/last-write-wins/invoices/3", stale with { Total = stale.Total + 10 }, etag: null)));
        await ReportAsync(http, 3, "no version check", codes, 110.00m, unexpected);

        var (current, version) = await GetAsync(http, 4);
        codes = await Task.WhenAll(Enumerable.Range(0, Editors).Select(_ =>
            PutAsync(http, "/invoices/4", current with { Total = current.Total + 10 }, version)));
        await ReportAsync(http, 4, "If-Match", codes, 110.00m, unexpected);
        if (codes.Count(c => c == 200) != 1 || codes.Count(c => c == 412) != Editors - 1) unexpected.Add("If-Match: expected one 200 and the rest 412");

        var attempts = await Task.WhenAll(Enumerable.Range(0, Editors).Select(_ => AddWithRetryAsync(http, 5)));
        Console.WriteLine($"   {"If-Match + retry:",-22} {Editors} x 200 after {attempts.Sum()} attempts in total");
        await ReportAsync(http, 5, "", null, 300.00m, unexpected);

        codes = await Task.WhenAll(Enumerable.Range(0, Editors).Select(async _ =>
        {
            using var response = await http.PostAsJsonAsync("/invoices/6/lines", new NewLine(10));
            return (int)response.StatusCode;
        }));
        await ReportAsync(http, 6, "one UPDATE statement", codes, 300.00m, unexpected);

        Console.WriteLine();
        Console.WriteLine("3. A save that does not say which version it edited");
        var (any, _) = await GetAsync(http, 2);
        var missing = await PutAsync(http, "/invoices/2", new InvoiceUpdate(any.Customer, any.PoNumber, any.DueDate, any.Total), etag: null);
        Console.WriteLine($"   PUT /invoices/2 without If-Match: {missing}");
        if (missing != 428) unexpected.Add($"PUT without If-Match answered {missing}, expected 428");

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

    private static async Task ReportAsync(HttpClient http, int id, string label, int[]? codes, decimal expectedTotal, List<string> unexpected)
    {
        var (invoice, _) = await GetAsync(http, id);
        var lost = (int)((300.00m - invoice.Total) / 10);
        var summary = codes is null ? "" : string.Join(", ", codes.GroupBy(c => c).OrderBy(g => g.Key).Select(g => $"{g.Count()} x {g.Key}"));
        var prefix = codes is null ? new string(' ', 26) : $"   {label + ":",-22} ";
        Console.WriteLine($"{prefix}{(summary.Length > 0 ? summary + ". " : "")}Total: {invoice.Total:0.00}, {lost} update(s) not applied");
        if (invoice.Total != expectedTotal) unexpected.Add($"invoice {id}: total {invoice.Total:0.00}, expected {expectedTotal:0.00}");
    }

    // What a well-behaved client does with a 412: read again, apply the change to the current version, retry.
    private static async Task<int> AddWithRetryAsync(HttpClient http, int id)
    {
        for (var attempt = 1; ; attempt++)
        {
            var (invoice, etag) = await GetAsync(http, id);
            var status = await PutAsync(http, $"/invoices/{id}", new InvoiceUpdate(invoice.Customer, invoice.PoNumber, invoice.DueDate, invoice.Total + 10), etag);
            if (status == 200) return attempt;
            if (status != 412 || attempt >= 200) throw new InvalidOperationException($"PUT /invoices/{id} answered {status}.");
            await Task.Delay(Random.Shared.Next(5, 40)); // a little jitter, so the retries do not collide again
        }
    }

    private static async Task<(InvoiceUpdate Invoice, string ETag)> GetAsync(HttpClient http, int id)
    {
        using var response = await http.GetAsync($"/invoices/{id}");
        response.EnsureSuccessStatusCode();
        var invoice = (await response.Content.ReadFromJsonAsync<InvoiceResponse>())!;
        return (new InvoiceUpdate(invoice.Customer, invoice.PoNumber, invoice.DueDate, invoice.Total), response.Headers.ETag!.ToString());
    }

    private static async Task<int> PutAsync(HttpClient http, string path, InvoiceUpdate update, string? etag)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, path) { Content = JsonContent.Create(update) };
        if (etag is not null) request.Headers.TryAddWithoutValidation("If-Match", etag);
        using var response = await http.SendAsync(request);
        return (int)response.StatusCode;
    }

    // The API recreates its schema on start, so give it a moment when both start together in Docker.
    private static async Task WaitForApiAsync(HttpClient http)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var response = await http.GetAsync("/health");
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
