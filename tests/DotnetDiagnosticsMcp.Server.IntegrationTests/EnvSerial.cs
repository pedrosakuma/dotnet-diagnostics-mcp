using Microsoft.Extensions.Logging;

namespace DotnetDiagnosticsMcp.Server.IntegrationTests;

/// <summary>xUnit collection used by tests that mutate process-global state
/// (environment variables) so they cannot trample each other under the default
/// parallel runner.</summary>
[CollectionDefinition(nameof(EnvSerial), DisableParallelization = true)]
public sealed class EnvSerial { }

/// <summary>RAII helper for scoped env-var mutation in tests.</summary>
internal sealed class EnvScope : IDisposable
{
    private readonly string _name;
    private readonly string? _previous;

    private EnvScope(string name, string? previous)
    {
        _name = name;
        _previous = previous;
    }

    public static EnvScope Set(string name, string? value)
    {
        var previous = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
        return new EnvScope(name, previous);
    }

    public static EnvScope Clear(string name) => Set(name, null);

    public void Dispose() => Environment.SetEnvironmentVariable(_name, _previous);
}

/// <summary>Minimal <see cref="ILoggerProvider"/> that buffers every emitted record
/// in memory. Sufficient for asserting on log shape ("warning fired exactly once",
/// "token value never appears in any record"). Thread-safe via the lock.</summary>
internal sealed class ListLoggerProvider : ILoggerProvider
{
    private readonly object _lock = new();
    private readonly List<Record> _records = new();

    public IReadOnlyList<Record> Records
    {
        get { lock (_lock) { return _records.ToArray(); } }
    }

    public ILogger CreateLogger(string categoryName) => new ListLogger(this, categoryName);

    public void Dispose() { }

    internal void Add(Record r)
    {
        lock (_lock) _records.Add(r);
    }

    internal readonly record struct Record(string Category, LogLevel Level, string Message);

    private sealed class ListLogger : ILogger
    {
        private readonly ListLoggerProvider _owner;
        private readonly string _category;
        public ListLogger(ListLoggerProvider owner, string category) { _owner = owner; _category = category; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            _owner.Add(new Record(_category, logLevel, formatter(state, exception)));
        }
    }
}
