using System.Net.Http.Json;

namespace OutboxSample;

// Issues invoices while things go wrong on purpose, and prints what the API saved,
// what reached the broker and what was emailed to the customer.
// It also checks the result: anything unexpected is reported and the process exits with code 1.
public static class Demo
{
    private const int Backlog = 60;

    public static async Task RunAsync(string baseUrl)
    {
        using var http = new HttpClient { BaseAddress = new Uri(baseUrl) };
        await WaitForApiAsync(http);
        var unexpected = new List<string>();

        Console.WriteLine("1. Publish after commit. The broker is down.");
        await http.PostAsync("/faults/broker?down=true", null);
        await IssueAsync(http, "/publish-after-commit/invoices", "INV-1001", expected: 503, unexpected);
        await http.PostAsync("/faults/broker?down=false", null);
        Print(await CheckAsync(http, "INV-1001", new(1, 0, 0, 0), unexpected), "saved, and the customer never gets it");

        Console.WriteLine();
        Console.WriteLine("2. Publish before commit. The request fails between the publish and the commit.");
        await IssueAsync(http, "/publish-before-commit/invoices?failBeforeCommit=true", "INV-1002", expected: 500, unexpected);
        Print(await CheckAsync(http, "INV-1002", new(0, 0, 1, 1), unexpected), "emailed, and the invoice does not exist");

        Console.WriteLine();
        Console.WriteLine("3. Outbox. The broker is down.");
        await http.PostAsync("/faults/broker?down=true", null);
        await IssueAsync(http, "/invoices", "INV-1003", expected: 201, unexpected);
        await Task.Delay(1200); // two ticks of the dispatcher, both refused by the broker
        Print(await CheckAsync(http, "INV-1003", new(1, 1, 0, 0), unexpected), "the invoice is issued; the event waits");
        Console.WriteLine("   the broker comes back");
        await http.PostAsync("/faults/broker?down=false", null);
        Print(await CheckAsync(http, "INV-1003", new(1, 0, 1, 1), unexpected), "delivered");

        Console.WriteLine();
        Console.WriteLine("4. Outbox. The dispatcher fails after publishing, before it marks the row as sent.");
        await http.PostAsync("/faults/dispatcher-fails-after-publish", null);
        await IssueAsync(http, "/invoices", "INV-1004", expected: 201, unexpected);
        Print(await CheckAsync(http, "INV-1004", new(1, 0, 2, 1), unexpected), "published twice, emailed once");

        Console.WriteLine();
        Console.WriteLine($"5. Outbox. {Backlog} invoices pile up while the broker is down, then two dispatchers drain them.");
        await http.PostAsync("/faults/broker?down=true", null);
        var codes = await Task.WhenAll(Enumerable.Range(1, Backlog).Select(async n =>
        {
            using var response = await http.PostAsJsonAsync("/invoices", new IssueInvoiceRequest($"INV-2{n:000}", 49.90m));
            return (int)response.StatusCode;
        }));
        Console.WriteLine($"   POST {"/invoices",-32} {codes.Count(c => c == 201)} x 201");
        await http.PostAsync("/faults/broker?down=false", null);

        var backlog = new List<StatsRow>();
        for (var attempt = 0; attempt < 50; attempt++)
        {
            backlog = (await http.GetFromJsonAsync<List<StatsRow>>("/stats"))!.Where(r => r.Number.StartsWith("INV-2")).ToList();
            if (backlog.Count == Backlog && backlog.All(r => r is { Invoices: 1, OutboxWaiting: 0, Events: 1, Emails: 1 })) break;
            await Task.Delay(200);
        }

        Console.WriteLine($"   invoices {backlog.Sum(r => r.Invoices)}, outbox {backlog.Sum(r => r.OutboxWaiting)}, events {backlog.Sum(r => r.Events)}, emails {backlog.Sum(r => r.Emails)}, published twice {backlog.Count(r => r.Events > 1)}");
        if (codes.Any(c => c != 201) || backlog.Count != Backlog || backlog.Any(r => r is not { Invoices: 1, OutboxWaiting: 0, Events: 1, Emails: 1 }))
        {
            unexpected.Add($"backlog: expected {Backlog} invoices, each with one event and one email");
        }

        // Not checked: how many arrive out of order depends on timing.
        var ordering = (await http.GetFromJsonAsync<OrderingReport>("/ordering?prefix=INV-2"))!;
        Console.WriteLine($"   events that reached the broker after a later one: {ordering.OutOfOrder} of {ordering.Events}");

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

    private sealed record Expected(int Invoices, int OutboxWaiting, int Events, int Emails);

    private static async Task IssueAsync(HttpClient http, string path, string number, int expected, List<string> unexpected)
    {
        using var response = await http.PostAsJsonAsync(path, new IssueInvoiceRequest(number, 49.90m));
        var status = (int)response.StatusCode;
        Console.WriteLine($"   POST {path.Split('?')[0],-32} {status}");
        if (status != expected) unexpected.Add($"{path} answered {status} for {number}, expected {expected}");
    }

    // The dispatcher works in the background, so give the expected state up to ten seconds to appear.
    private static async Task<StatsRow> CheckAsync(HttpClient http, string number, Expected expected, List<string> unexpected)
    {
        var row = new StatsRow(number, 0, 0, 0, 0);
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var rows = (await http.GetFromJsonAsync<List<StatsRow>>("/stats"))!;
            row = rows.FirstOrDefault(r => r.Number == number) ?? new StatsRow(number, 0, 0, 0, 0);
            if (new Expected(row.Invoices, row.OutboxWaiting, row.Events, row.Emails) == expected)
            {
                return row;
            }

            await Task.Delay(200);
        }

        unexpected.Add($"{number}: {Describe(row)}; expected {Describe(new StatsRow(number, expected.Invoices, expected.OutboxWaiting, expected.Events, expected.Emails))}");
        return row;
    }

    private static void Print(StatsRow row, string note) => Console.WriteLine($"   {row.Number}: {Describe(row)}   <- {note}");

    private static string Describe(StatsRow row) =>
        $"invoices {row.Invoices}, outbox {row.OutboxWaiting}, events {row.Events}, emails {row.Emails}";

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
