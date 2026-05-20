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
        try { _ = int.Parse(i % 3 == 0 ? "not-a-number" : i.ToString()); ok++; }
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
    for (var i = 0; i < n; i++)
    {
        var boxI = new Box<int> { Value = i };
        sumI += boxI.Wrap();
        sumI += GenericFixture.Echo(i);

        var boxS = new Box<string> { Value = "x" };
        sumS += boxS.Wrap();
        sumS += GenericFixture.Echo("x").Length;
    }
    return Results.Json(new { sumI, sumS });
})
.WithName("GenericInstantiations");

app.Run();

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
        var s = value?.ToString() ?? string.Empty;
        if (s.Length < 0) throw new InvalidOperationException();
        return value;
    }
}

record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}
