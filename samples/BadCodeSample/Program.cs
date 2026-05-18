using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;

// BadCodeSample — a deliberately-broken minimal API used to exercise the
// dotnet-dbg-mcp tools. Every endpoint triggers a different anti-pattern that
// should be detectable end-to-end via the MCP server (counters, cpu sampling,
// exceptions, GC events, EventSource passthrough, dump).
//
// See docs/bad-code-scenarios.md for the per-endpoint investigation playbook.

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient("slow", c =>
{
    c.Timeout = TimeSpan.FromSeconds(30);
});

var app = builder.Build();

var leakedBuffers = new List<byte[]>();
var lockObject = new object();

app.MapGet("/", () => Results.Ok(new
{
    name = "BadCodeSample",
    endpoints = new[]
    {
        "/cpu-burn?ms=2000",
        "/leak?mb=4",
        "/exceptions?count=200",
        "/sync-over-async?n=20",
        "/lock-contention?threads=32&ms=1500",
        "/loh-alloc?count=20",
        "/slow-http?url=https://httpbin.org/delay/3",
    },
}));

// 1. CPU burn — detect with snapshot_counters + collect_cpu_sample
app.MapGet("/cpu-burn", (int? ms) =>
{
    var budget = TimeSpan.FromMilliseconds(ms ?? 2000);
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var input = Encoding.UTF8.GetBytes("dotnet-dbg-mcp cpu burn payload");
    long iterations = 0;
    while (sw.Elapsed < budget)
    {
        for (var i = 0; i < 10_000; i++)
        {
            _ = SHA256.HashData(input);
            iterations++;
        }
    }
    return Results.Ok(new { iterations, elapsedMs = sw.ElapsedMilliseconds });
});

// 2. Managed memory leak — detect with snapshot_counters (gc-heap-size, gen2)
//    + collect_gc_events + collect_process_dump
app.MapGet("/leak", (int? mb) =>
{
    var size = Math.Clamp(mb ?? 4, 1, 64) * 1024 * 1024;
    var buf = new byte[size];
    Random.Shared.NextBytes(buf);
    lock (leakedBuffers)
    {
        leakedBuffers.Add(buf);
    }
    return Results.Ok(new { addedMb = size / (1024 * 1024), totalBuffers = leakedBuffers.Count });
});

// 3. Exception storm — detect with snapshot_counters + collect_exceptions
app.MapGet("/exceptions", (int? count) =>
{
    var n = Math.Clamp(count ?? 200, 1, 5_000);
    var caught = 0;
    for (var i = 0; i < n; i++)
    {
        try
        {
            _ = int.Parse("not-a-number", CultureInfo.InvariantCulture);
        }
        catch (FormatException)
        {
            caught++;
        }
    }
    return Results.Ok(new { caught });
});

// 4. Sync-over-async / thread pool starvation — detect with counters
//    (threadpool-queue-length, threadpool-thread-count) + collect_cpu_sample
app.MapGet("/sync-over-async", (IHttpClientFactory http, int? n) =>
{
    var clients = Math.Clamp(n ?? 20, 1, 200);
    var tasks = new List<Task>();
    for (var i = 0; i < clients; i++)
    {
        tasks.Add(Task.Run(() =>
        {
            using var client = http.CreateClient("slow");
            client.Timeout = TimeSpan.FromSeconds(5);
            try
            {
                _ = client.GetAsync("https://example.com").GetAwaiter().GetResult();
            }
            catch
            {
            }
        }));
    }
    Task.WaitAll(tasks.ToArray());
    return Results.Ok(new { dispatched = clients });
});

// 5. Monitor lock contention — detect with counters
//    (monitor-lock-contention-count) + collect_cpu_sample
app.MapGet("/lock-contention", (int? threads, int? ms) =>
{
    var threadCount = Math.Clamp(threads ?? 32, 2, 256);
    var budget = TimeSpan.FromMilliseconds(Math.Clamp(ms ?? 1500, 100, 30_000));
    var stop = DateTime.UtcNow + budget;
    var tasks = new List<Task>();
    long entered = 0;
    for (var i = 0; i < threadCount; i++)
    {
        tasks.Add(Task.Run(() =>
        {
            while (DateTime.UtcNow < stop)
            {
                lock (lockObject)
                {
                    Interlocked.Increment(ref entered);
                    Thread.SpinWait(5_000);
                }
            }
        }));
    }
    Task.WaitAll(tasks.ToArray());
    return Results.Ok(new { threads = threadCount, entered });
});

// 6. LOH allocation churn — detect with counters (loh-size, gen2-gc-count)
//    + collect_gc_events
app.MapGet("/loh-alloc", (int? count) =>
{
    var n = Math.Clamp(count ?? 20, 1, 500);
    long allocated = 0;
    for (var i = 0; i < n; i++)
    {
        var buf = new byte[200_000];
        Random.Shared.NextBytes(buf);
        allocated += buf.Length;
    }
    return Results.Ok(new { iterations = n, allocatedBytes = allocated });
});

// 7. Slow outbound HTTP — detect with collect_event_source name=System.Net.Http
app.MapGet("/slow-http", async (IHttpClientFactory http, string? url) =>
{
    using var client = http.CreateClient("slow");
    var target = url ?? "https://httpbin.org/delay/3";
    var sw = System.Diagnostics.Stopwatch.StartNew();
    HttpStatusCode? status = null;
    try
    {
        using var resp = await client.GetAsync(target);
        status = resp.StatusCode;
    }
    catch (Exception ex)
    {
        return Results.Ok(new { url = target, elapsedMs = sw.ElapsedMilliseconds, error = ex.GetType().Name });
    }
    return Results.Ok(new { url = target, elapsedMs = sw.ElapsedMilliseconds, status });
});

app.Run();
