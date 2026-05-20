using DotnetDiagnosticsMcp.Core.Counters;

namespace DotnetDiagnosticsMcp.Server.Tools;

/// <summary>
/// Filters a <see cref="CounterSnapshot"/> down to the "headline" counters the LLM needs to
/// triage cheaply at <see cref="DotnetDiagnosticsMcp.Core.Collection.SamplingDepth.Summary"/>:
/// CPU, working set, GC heap, gen-2 collections, thread-pool depth, exceptions/sec,
/// monitor lock contention, plus ASP.NET Core request rate / failures. Everything else stays
/// reachable via the handle store (<c>query_collection(handle, view=byProvider)</c>).
/// </summary>
internal static class HeadlineCounters
{
    private static readonly HashSet<(string Provider, string Name)> Headline = new()
    {
        ("System.Runtime", "cpu-usage"),
        ("System.Runtime", "working-set"),
        ("System.Runtime", "gc-heap-size"),
        ("System.Runtime", "gen-2-gc-count"),
        ("System.Runtime", "threadpool-thread-count"),
        ("System.Runtime", "threadpool-queue-length"),
        ("System.Runtime", "exception-count"),
        ("System.Runtime", "monitor-lock-contention-count"),
        ("Microsoft.AspNetCore.Hosting", "requests-per-second"),
        ("Microsoft.AspNetCore.Hosting", "failed-requests"),
        ("Microsoft.AspNetCore.Hosting", "current-requests"),
        ("Microsoft-AspNetCore-Server-Kestrel", "connections-per-second"),
    };

    public static IReadOnlyList<CounterValue> Filter(IReadOnlyList<CounterValue> all)
    {
        var hits = new List<CounterValue>(Headline.Count);
        foreach (var c in all)
        {
            if (Headline.Contains((c.Provider, c.Name))) hits.Add(c);
        }
        return hits;
    }
}
