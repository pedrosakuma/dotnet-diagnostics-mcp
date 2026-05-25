using System.ComponentModel;
using ModelContextProtocol.Server;

namespace DotnetDiagnosticsMcp.Server.Resources;

/// <summary>
/// Read-only resources surfaced to MCP clients. Resources are intended for content the model
/// should be able to consume on demand (e.g. an investigation playbook), without it counting
/// against the limited "≤10 tools" tool budget.
/// </summary>
[McpServerResourceType]
public sealed class InvestigationGuideResources
{
    [McpServerResource(
        UriTemplate = "diag://guides/investigation",
        Name = "investigation-guide",
        Title = "Diagnostic investigation guide",
        MimeType = "text/markdown")]
    [Description(
        "Detailed playbook for using this server's tools to investigate common .NET performance " +
        "problems (high CPU, GC pressure, exception storms, latency spikes). Read this when the " +
        "compact server `instructions` aren't enough to steer the investigation.")]
    public static string InvestigationGuide() => GuideMarkdown;

    private const string GuideMarkdown = """
        # .NET diagnostic investigation guide

        This server attaches to a running .NET process via its diagnostic IPC socket and exposes
        a set of read-only collectors plus one write-side tool (`collect_process_dump`). Use this
        guide to choose which tool to call, in what order, and how to interpret the response.

        ## 1. Discover the target

        ```text
        inspect_process(view="list")              # discover pids attachable on this host
        inspect_process(view="info", processId)   # confirm a specific pid is still alive
        inspect_process(view="capabilities", pid) # CoreCLR vs NativeAOT, gcdump/CPU-sampling support
        ```

        Capabilities matter: NativeAOT does **not** support CPU sampling or `gcdump`. Plan around
        counters + custom EventSource + dumps instead.

        ## 2. Establish a baseline

        Always start with `collect_events(kind="counters")` over 5–10 seconds. It is the cheapest signal and
        covers:

        - `System.Runtime` — cpu-usage, working-set, gc-heap-size, gen-*-size,
          monitor-lock-contention-count, threadpool-queue-length, exception-count, …
        - `Microsoft.AspNetCore.Hosting` — requests/sec, current-requests, failed-requests
        - `Microsoft-AspNetCore-Server-Kestrel` — connections, queue depth

        ## 3. Branch by symptom

        | Symptom                                  | Tool                                                        | Why                                                       |
        |------------------------------------------|-------------------------------------------------------------|-----------------------------------------------------------|
        | `cpu-usage` ≥ 70%                        | `collect_sample(kind="cpu")`                                | Identify hot methods via the SampleProfiler EventSource   |
        | Growing `gc-heap-size`, frequent gen-2   | `collect_events(kind="gc")`                                 | Confirm pause distribution before pulling a heap dump     |
        | Many `monitor-lock-contention-count`     | `collect_events(kind="event_source")` (TPL, Threading)      | Confirm contention pattern                                |
        | HTTP/network anomalies                   | `collect_events(kind="event_source", provider=System.Net.Http)` | Activities + response status                          |
        | Exception storms                         | `collect_events(kind="exceptions")`                         | Start the session **before** the workload                 |
        | Need ground truth / out-of-band analysis | `collect_process_dump`                                      | Heaviest tool — Mini < Triage < WithHeap < Full           |

        ## 4. Always bound the cost

        Every collector accepts a `durationSeconds` and most accept `maxRecent` / `topN` /
        `maxEvents`. Prefer the shortest window that answers the question. Default windows are
        intentionally conservative — increase only when the first attempt returns nothing
        actionable.

        ## 5. Follow the hints

        Every response includes a `summary`, the typed `data` payload, and a `hints` array. The
        hints describe the recommended next call given what the data showed (e.g. "cpu-usage=82%
        — run `collect_sample(kind=\"cpu\")`"). When in doubt, follow the first hint.

        ## 6. Error recovery

        Errors are returned as structured `DiagnosticError` records with a `Kind` field
        (`InvalidArgument`, `ProcessNotFound`, `EndpointUnavailable`, …) and at least one hint
        describing the recovery. The most common one is `EndpointUnavailable` — caused by a UID
        mismatch between the sidecar and the target. Re-run `inspect_process(view="list")` after fixing
        the UID.

        ## Non-goals

        - This server does not modify the target. Anything requiring instrumentation lives
          elsewhere.
        - This server does not analyze dumps on its own — `collect_process_dump` writes the file,
          you analyze it with `dotnet-dump analyze` (or a future companion MCP).
        """;
}
