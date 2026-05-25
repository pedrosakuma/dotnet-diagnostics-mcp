using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

var summaries = new[]
{
    "Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
};

app.MapGet("/weatherforecast", () =>
{
    var forecast =  Enumerable.Range(1, 5).Select(index =>
        new WeatherForecast
        (
            DateOnly.FromDateTime(DateTime.Now.AddDays(index)),
            Random.Shared.Next(-20, 55),
            summaries[Random.Shared.Next(summaries.Length)]
        ))
        .ToArray();
    return forecast;
})
.WithName("GetWeatherForecast");

// === Intentional diagnostic targets (for dotnet-diagnostics-mcp demos) ===
// Each of the following endpoints exhibits a different runtime pathology.
// The bug is deliberately a little obfuscated so a profiler-driven walkthrough
// has something to actually discover.

// O(n²) string concat hidden inside an innocuous-looking template renderer.
app.MapGet("/render", (int? count) =>
{
    var n = count ?? 5_000;
    var sb = string.Empty;
    for (var i = 0; i < n; i++)
    {
        sb += $"line-{i};"; // accidental quadratic: every += allocates the full prefix
    }
    return Results.Text(sb.Substring(0, Math.Min(64, sb.Length)));
})
.WithName("RenderLines");

// Slow regex on user input — classic backtracking blowup.
app.MapGet("/validate", (string? email) =>
{
    var input = email ?? new string('a', 30) + "!";
    // Catastrophic backtracking pattern on bad input.
    var pattern = "^(a+)+$";
    var ok = System.Text.RegularExpressions.Regex.IsMatch(input, pattern);
    return Results.Json(new { input, ok });
})
.WithName("ValidateEmail");

// Memory leak: every call appends a 1 MiB byte[] to a static list.
var cache = new List<byte[]>();
var sampleActivitySource = new ActivitySource("CoreClrSample.Activities");
app.MapGet("/leak", () =>
{
    cache.Add(new byte[1_048_576]);
    return Results.Json(new { retainedMb = cache.Count });
})
.WithName("LeakOneMB");

// Exception-storm: control flow via int.Parse on bad input inside a hot loop.
app.MapGet("/parse", () =>
{
    var ok = 0;
    var ko = 0;
    for (var i = 0; i < 500; i++)
    {
        try { _ = int.Parse(i % 3 == 0 ? "not-a-number" : i.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture); ok++; }
        catch (FormatException) { ko++; }
    }
    return Results.Json(new { ok, ko });
})
.WithName("ParseThings");

// Generic fixture (issue #21 — handoff contract closed instantiations). The hot loop
// exercises both type-level (Box<int>, Box<string>) and method-level (Echo<int>,
// Echo<string>) closed generics so the CPU sampler emits two distinct MethodIdentity
// rows per open MethodDef.
app.MapGet("/generics", (int? iterations) =>
{
    var n = iterations ?? 50_000;
    var sumI = 0L;
    var sumS = 0L;
    // Use a non-trivial string so Box<string>.Wrap and Echo<string> actually do work
    // on each call — otherwise the body returns faster than the CPU sampling interval
    // and the closed instantiations never land in any captured stack frame.
    const string payload = "abcdefghijklmnopqrstuvwxyz";
    for (var i = 0; i < n; i++)
    {
        var boxI = new Box<int> { Value = i };
        sumI += boxI.Wrap();
        sumI += GenericFixture.Echo(i);

        var boxS = new Box<string> { Value = payload };
        sumS += boxS.Wrap();
        sumS += GenericFixture.Echo(payload).Length;
    }
    return Results.Json(new { sumI, sumS });
})
.WithName("GenericInstantiations");

app.MapGet("/activity", async (int? delayMs) =>
{
    var delay = Math.Clamp(delayMs ?? 50, 1, 2_000);

    using var parent = sampleActivitySource.StartActivity("CoreClrSample.Outer");
    parent?.SetTag("endpoint", "/activity");
    parent?.SetTag("sample.delay_ms", delay);
    parent?.SetTag("sample.kind", "demo");

    await Task.Delay(delay);

    using (var child = sampleActivitySource.StartActivity("CoreClrSample.Inner"))
    {
        child?.SetTag("child", "true");
        child?.SetTag("db.system", "sample");
        await Task.Delay(10);
    }

    return Results.Json(new
    {
        source = sampleActivitySource.Name,
        traceId = parent?.TraceId.ToString(),
        spanId = parent?.SpanId.ToString(),
        id = parent?.Id,
    });
})
.WithName("EmitActivityTrace");

// Async hang fixture (issue #117 — heap async-state inspection). Each request starts
// a few nested async methods that await a never-completing task; the returned top-level
// Task is retained in a dictionary so the state machines stay rooted for heap inspection.
var pendingAsyncChains = new ConcurrentDictionary<int, Task<string>>();
app.MapGet("/async-pending", (int? count) =>
{
    var n = Math.Clamp(count ?? 1, 1, 32);
    var started = new List<int>(n);
    for (var i = 0; i < n; i++)
    {
        var id = AsyncFixture.NextId();
        pendingAsyncChains[id] = AsyncFixture.StartAsync(id);
        started.Add(id);
    }

    return Results.Json(new { started, active = pendingAsyncChains.Count });
})
.WithName("AsyncPending");

// ThreadPool fixture for query_snapshot(view="threadpool"). Queue a mix of
// global and prefer-local work items that block behind a shared gate long enough for
// a live snapshot to observe non-zero pending counts/queue depths.
app.MapGet("/threadpool/queue", (int? globalItems, int? localItems, int? blockMs) =>
{
    var global = Math.Clamp(globalItems ?? 128, 0, 4_096);
    var local = Math.Clamp(localItems ?? 128, 0, 4_096);
    var delayMs = Math.Clamp(blockMs ?? 3_000, 100, 30_000);
    var deadline = Stopwatch.GetTimestamp() + (long)delayMs * Stopwatch.Frequency / 1_000;

    // Queue local work from inside a worker so preferLocal=true lands on an actual
    // work-stealing queue without stalling the HTTP response path behind those items.
    ThreadPool.UnsafeQueueUserWorkItem(static state =>
    {
        var (localCount, innerDeadline) = ((int LocalCount, long Deadline))state!;
        for (var i = 0; i < localCount; i++)
        {
            ThreadPool.UnsafeQueueUserWorkItem(static workDeadline => BusySpin((long)workDeadline!), innerDeadline, preferLocal: true);
        }

        BusySpin(innerDeadline);
    }, (local, deadline), preferLocal: false);

    for (var i = 0; i < global; i++)
    {
        ThreadPool.UnsafeQueueUserWorkItem(static workDeadline => BusySpin((long)workDeadline!), deadline, preferLocal: false);
    }

    return Results.Accepted($"/threadpool/queue?globalItems={global}&localItems={local}", new
    {
        globalQueued = global,
        localQueued = local,
        blockMs = delayMs,
    });
})
.WithName("QueueThreadPoolWork");

app.Run();

static void BusySpin(long deadline)
{
    var spinner = new SpinWait();
    while (Stopwatch.GetTimestamp() < deadline)
    {
        spinner.SpinOnce();
    }
}

sealed class Box<T>
{
    public T? Value { get; set; }
    [MethodImpl(MethodImplOptions.NoInlining)]
    public int Wrap()
    {
        // Trivial work that dominates samples when called in a tight loop.
        var s = Value?.ToString() ?? string.Empty;
        var h = 0;
        for (var i = 0; i < s.Length; i++) h = unchecked(h * 31 + s[i]);
        return h;
    }
}

static class GenericFixture
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static T Echo<T>(T value)
    {
        // Force the JIT to specialize so each instantiation shows up as a distinct MethodID.
        // The inner loop guarantees the body is heavy enough that the CPU sampler can land
        // a stack frame inside it (without it the call returns faster than the sampling
        // interval and method-level closed generics never surface in the trace).
        var s = value?.ToString() ?? string.Empty;
        var h = 0;
        for (var i = 0; i < s.Length; i++) h = unchecked(h * 31 + s[i]);
        if (h == int.MinValue) throw new InvalidOperationException();
        return value;
    }
}

static class AsyncFixture
{
    private static readonly TaskCompletionSource<string> Never = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static int _nextId;

    public static int NextId() => Interlocked.Increment(ref _nextId);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static Task<string> StartAsync(int id) => OuterAsync(id);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<string> OuterAsync(int id)
        => await MiddleAsync(id);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<string> MiddleAsync(int id)
        => await LeafAsync(id);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<string> LeafAsync(int id)
    {
        await Never.Task.ConfigureAwait(false);
        return $"never-{id}";
    }
}

sealed record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}
