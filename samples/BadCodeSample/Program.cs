using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

// BadCodeSample — a deliberately-broken minimal API used to exercise the
// dotnet-diagnostics-mcp tools. Every endpoint triggers a different anti-pattern that
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
var leakedFiles = new List<FileStream>();
var leakedSockets = new List<LeakedSocketConnection>();
var badCodeEndpoints = new[]
{
    "/cpu-burn?ms=2000",
    "/leak?mb=4",
    "/exceptions?count=200",
    "/sync-over-async?n=20",
    "/lock-contention?threads=32&ms=1500",
    "/loh-alloc?count=20",
    "/slow-http?url=https://httpbin.org/delay/3",
    "/fd-leak?count=64",
    "/socket-leak?count=32&host=loopback",
    "/meter-spam?count=5&kind=counter",
    "/log-spam?count=200&level=warning",
    "/jit-pressure?count=200",
};
var lockObject = new object();
var meterFactory = app.Services.GetRequiredService<IMeterFactory>();
var meter = meterFactory.Create("BadCodeSample");
var ordersTotal = meter.CreateCounter<long>("orders.total", unit: "{orders}", description: "Synthetic business counter for tests.");
var workDuration = meter.CreateHistogram<double>("work.duration", unit: "ms", description: "Synthetic histogram for meter tests.");
using var loopbackCloser = new LoopbackCloseServer();
loopbackCloser.Start();

app.MapGet("/", () => Results.Ok(new
{
    name = "BadCodeSample",
    endpoints = badCodeEndpoints,
}));

// 1. CPU burn — detect with collect_events(kind="counters") + collect_sample(kind="cpu")
app.MapGet("/cpu-burn", (int? ms) =>
{
    var budget = TimeSpan.FromMilliseconds(ms ?? 2000);
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var input = Encoding.UTF8.GetBytes("dotnet-diagnostics-mcp cpu burn payload");
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

// 2. Managed memory leak — detect with collect_events(kind="counters") (gc-heap-size, gen2)
//    + collect_events(kind="gc") + collect_process_dump
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

// 3. Exception storm — detect with collect_events(kind="counters") + collect_events(kind="exceptions")
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
//    (threadpool-queue-length, threadpool-thread-count) + collect_sample(kind="cpu")
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
//    (monitor-lock-contention-count) + collect_sample(kind="cpu")
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
//    + collect_events(kind="gc")
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

// 7. Slow outbound HTTP — detect with collect_events(kind="event_source") name=System.Net.Http
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

// 8. Meter API spam — detect with collect_events(kind="counters", meters=["BadCodeSample"])
app.MapGet("/meter-spam", (int? count, string? kind) =>
{
    var samples = Math.Clamp(count ?? 10, 1, 5_000);
    var mode = string.Equals(kind, "histogram", StringComparison.OrdinalIgnoreCase) ? "histogram" : "counter";
    for (var i = 0; i < samples; i++)
    {
        var tags = new TagList
        {
            { "series", $"series-{i}" },
            { "kind", mode },
        };

        if (mode == "histogram")
        {
            workDuration.Record(10 + i, tags);
        }
        else
        {
            ordersTotal.Add(1, tags);
        }
    }

    return Results.Ok(new { samples, kind = mode });
});

// 9. FD leak — detect with inspect_process(view="resources") (FdCount / nofile fraction)
app.MapGet("/fd-leak", (int? count) =>
{
    var n = Math.Clamp(count ?? 64, 1, 1_024);
    var path = Environment.ProcessPath ?? typeof(Program).Assembly.Location;
    var opened = 0;
    lock (leakedFiles)
    {
        for (var i = 0; i < n; i++)
        {
            leakedFiles.Add(File.OpenRead(path));
            opened++;
        }
    }

    return Results.Ok(new { opened, totalLeaked = leakedFiles.Count, path });
});

// 10. Socket leak / CLOSE_WAIT growth — detect with inspect_process(view="resources")
app.MapGet("/socket-leak", async (int? count, string? host) =>
{
    var n = Math.Clamp(count ?? 32, 1, 256);
    var target = string.IsNullOrWhiteSpace(host) ? "loopback" : host.Trim();
    var leaked = 0;

    for (var i = 0; i < n; i++)
    {
        var connection = target.Equals("loopback", StringComparison.OrdinalIgnoreCase)
            ? await LeakLoopbackSocketAsync(loopbackCloser.Port)
            : await LeakRemoteSocketAsync(target);
        lock (leakedSockets)
        {
            leakedSockets.Add(connection);
        }
        leaked++;
    }

    return Results.Ok(new { leaked, totalLeaked = leakedSockets.Count, host = target, loopbackPort = loopbackCloser.Port });
});

// 11. ILogger warning/error storm — detect with collect_events(kind="logs")
app.MapGet("/log-spam", (ILoggerFactory loggerFactory, int? count, string? level) =>
{
    var n = Math.Clamp(count ?? 200, 1, 5_000);
    var parsedLevel = Enum.TryParse<LogLevel>(level, ignoreCase: true, out var explicitLevel)
        && explicitLevel is >= LogLevel.Trace and <= LogLevel.Critical
        ? explicitLevel
        : LogLevel.Warning;
    var logger = loggerFactory.CreateLogger("BadCodeSample.LogSpam");

    using (logger.BeginScope(
        "UserEmail {UserEmail} Password {Password} ScopeName {ScopeName}",
        "person@example.com",
        "Password=super-secret",
        "BadCode.LogSpam"))
    {
        for (var i = 0; i < n; i++)
        {
            var eventId = new EventId(10_000 + i, $"LogSpam{parsedLevel}");
            if (parsedLevel >= LogLevel.Error)
            {
                logger.Log(parsedLevel, eventId, new InvalidOperationException($"boom-{i}"), "Log spam {Level} #{Index}", parsedLevel, i);
            }
            else
            {
                logger.Log(parsedLevel, eventId, "Log spam {Level} #{Index}", parsedLevel, i);
            }
        }
    }

    return Results.Ok(new { count = n, level = parsedLevel.ToString() });
});

// 12. JIT pressure / cold-start compilation — detect with collect_events(kind="jit")
app.MapGet("/jit-pressure", (int? count) =>
{
    var n = Math.Clamp(count ?? 200, 1, 2_000);
    long checksum = 0;
    for (var i = 0; i < n; i++)
    {
        checksum += CreateAndInvokeJitPressureMethod(i);
    }

    return Results.Ok(new { count = n, checksum });
});

app.Run();

static int CreateAndInvokeJitPressureMethod(int seed)
{
    var method = new DynamicMethod($"JitPressureDynamicMethod{seed:D4}", typeof(int), new[] { typeof(int) }, typeof(Program).Module, skipVisibility: true);
    var il = method.GetILGenerator();
    il.Emit(OpCodes.Ldarg_0);
    for (var i = 0; i < 24; i++)
    {
        il.Emit(OpCodes.Ldc_I4, seed + i + 1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Ldc_I4, (i % 7) + 1);
        il.Emit(OpCodes.Xor);
    }

    il.Emit(OpCodes.Ret);

    var handler = method.CreateDelegate<Func<int, int>>();
    return handler(seed);
}

static async Task<LeakedSocketConnection> LeakLoopbackSocketAsync(int port)
{
    var client = new TcpClient();
    await client.ConnectAsync(IPAddress.Loopback, port);
    return await CompleteCloseWaitHandshakeAsync(client, "localhost", "/");
}

static async Task<LeakedSocketConnection> LeakRemoteSocketAsync(string host)
{
    string requestHost;
    string requestPath;
    int port;

    if (Uri.TryCreate(host, UriKind.Absolute, out var absolute) && absolute.Scheme == Uri.UriSchemeHttp)
    {
        requestHost = absolute.Host;
        requestPath = string.IsNullOrEmpty(absolute.PathAndQuery) ? "/" : absolute.PathAndQuery;
        port = absolute.Port;
    }
    else
    {
        requestHost = host;
        requestPath = "/get";
        port = 80;
    }

    var client = new TcpClient();
    await client.ConnectAsync(requestHost, port);
    return await CompleteCloseWaitHandshakeAsync(client, requestHost, requestPath);
}

static async Task<LeakedSocketConnection> CompleteCloseWaitHandshakeAsync(TcpClient client, string host, string path)
{
    var stream = client.GetStream();
    var request = Encoding.ASCII.GetBytes($"GET {path} HTTP/1.1\r\nHost: {host}\r\nConnection: close\r\n\r\n");
    await stream.WriteAsync(request);

    var buffer = new byte[512];
    while (true)
    {
        var read = await stream.ReadAsync(buffer);
        if (read == 0)
        {
            break;
        }
    }

    return new LeakedSocketConnection(client, stream);
}

sealed class LeakedSocketConnection(TcpClient client, NetworkStream stream)
{
    public TcpClient Client { get; } = client;
    public NetworkStream Stream { get; } = stream;
}

sealed class LoopbackCloseServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptLoop;

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public void Start()
    {
        _listener.Start();
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        try
        {
            _acceptLoop?.GetAwaiter().GetResult();
        }
        catch
        {
        }
        _cts.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient? client = null;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token);
                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        try
                        {
                            using var stream = client.GetStream();
                            var buffer = new byte[256];
                            _ = await stream.ReadAsync(buffer, _cts.Token);
                        }
                        catch
                        {
                        }
                    }
                }, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
        }
    }
}
