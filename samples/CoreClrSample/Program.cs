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

app.Run();

record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}
