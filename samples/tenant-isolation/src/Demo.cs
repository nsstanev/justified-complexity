using System.Net.Http.Json;

namespace TenantIsolation;

// Signs in as Acme, calls every endpoint and counts the rows that belong to another tenant.
// It also checks the result: anything unexpected is reported and the process exits with code 1.
public static class Demo
{
    public static async Task RunAsync(string baseUrl)
    {
        using var http = new HttpClient { BaseAddress = new Uri(baseUrl) };
        await WaitForApiAsync(http);
        var unexpected = new List<string>();

        Console.WriteLine("Two tenants, Acme and Globex, with 3 payslips each. Every request below is made as Acme.");

        Console.WriteLine();
        Console.WriteLine("1. A WHERE clause in every query");
        await ReadAsync(http, "/manual/payslips", "acme", 3, 0, "", unexpected);
        await ReadAsync(http, "/manual/payslips/search?employee=an", "acme", 6, 3, "this query forgot the WHERE", unexpected);

        Console.WriteLine();
        Console.WriteLine("2. An EF Core global query filter");
        await ReadAsync(http, "/filtered/payslips", "acme", 3, 0, "", unexpected);
        await ReadAsync(http, "/filtered/payslips/search?employee=an", "acme", 3, 0, "", unexpected);
        await ReadAsync(http, "/filtered/payslips/highest", "acme", 6, 3, "raw SQL does not go through the filter", unexpected);

        Console.WriteLine();
        Console.WriteLine("3. The same code on a database login with row-level security");
        await ReadAsync(http, "/rls/payslips", "acme", 3, 0, "", unexpected);
        await ReadAsync(http, "/rls/payslips/search?employee=an", "acme", 3, 0, "", unexpected);
        await ReadAsync(http, "/rls/payslips/highest", "acme", 3, 0, "the same raw SQL", unexpected);

        Console.WriteLine();
        Console.WriteLine("4. A write that names another tenant (the body says Globex)");
        await WriteAsync(http, "/rls/payslips", 403, "the database refused the row", unexpected);
        await WriteAsync(http, "/filtered/payslips", 201, "a payslip planted in Globex", unexpected);

        Console.WriteLine();
        Console.WriteLine("5. No tenant at all, as in a background job");
        await ReadAsync(http, "/filtered/payslips/highest", null, 7, 0, "every tenant's rows", unexpected);
        await ReadAsync(http, "/rls/payslips/highest", null, 0, 0, "nothing: no tenant, no rows", unexpected);

        Console.WriteLine();
        var tables = (await http.GetFromJsonAsync<List<string>>("/admin/unprotected-tables"))!;
        Console.WriteLine($"6. Tables with a tenant_id column and no row-level security: {(tables.Count == 0 ? "none" : string.Join(", ", tables))}");
        if (tables is not ["leave_requests"]) unexpected.Add("expected the check to report leave_requests");

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

    private static async Task ReadAsync(HttpClient http, string path, string? tenant, int expectedRows, int expectedForeign, string note, List<string> unexpected)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (tenant is not null) request.Headers.Add("X-Tenant", tenant);
        using var response = await http.SendAsync(request);
        // A failed request is not an empty result: without this check an error would pass as "no rows".
        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"   GET  {path.Split('?')[0],-28} failed with {(int)response.StatusCode}");
            unexpected.Add($"{path}: the request failed with {(int)response.StatusCode}; expected 200");
            return;
        }

        var rows = (await response.Content.ReadFromJsonAsync<List<PayslipRow>>())!;
        var foreign = rows.Count(r => r.TenantId != Tenants.Acme);

        // With no tenant there is no "own" and "other": count the tenants instead.
        var what = tenant is null
            ? $"{rows.Count} row(s) from {rows.Select(r => r.TenantId).Distinct().Count()} tenant(s)"
            : $"{rows.Count} row(s), {foreign} from another tenant";
        if (tenant is null) foreign = expectedForeign;

        Console.WriteLine($"   GET  {path.Split('?')[0],-28} {what}{(note.Length > 0 ? $"   <- {note}" : "")}");
        if (rows.Count != expectedRows || foreign != expectedForeign)
        {
            unexpected.Add($"{path}: {rows.Count} row(s), {foreign} from another tenant; expected {expectedRows} and {expectedForeign}");
        }
    }

    private static async Task WriteAsync(HttpClient http, string path, int expected, string note, List<string> unexpected)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(new NewPayslip(Tenants.Globex, "Planted Row", "2026-08", 1.00m)),
        };
        request.Headers.Add("X-Tenant", "acme");
        using var response = await http.SendAsync(request);
        var status = (int)response.StatusCode;

        Console.WriteLine($"   POST {path,-28} {status}   <- {note}");
        if (status != expected) unexpected.Add($"POST {path} answered {status}, expected {expected}");
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
