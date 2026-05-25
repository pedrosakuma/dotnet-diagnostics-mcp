using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DotnetDiagnosticsMcp.Server.Prompts;

/// <summary>
/// Server-side Prompts that pre-package an investigation strategy for the most common .NET
/// performance complaints. The LLM still drives the tool calls, but the prompt gives it the
/// curated playbook without burning context on a long system prompt — and clients can surface
/// each prompt as a one-click action.
/// </summary>
/// <remarks>
/// All prompts are sourced from <c>docs/investigation-playbooks.md</c> (issue #44). They are
/// returned with a single <see cref="PromptMessage"/> of role <see cref="Role.User"/> (the
/// content is an instruction to the LLM) and <see cref="Annotations.Audience"/> set to
/// <see cref="Role.Assistant"/> so that MCP clients which separate user-facing templates from
/// assistant-facing context route them correctly.
/// </remarks>
[McpServerPromptType]
public sealed class DiagnosticPrompts
{
    private const string ProcessIdParamDescription =
        "Operating system process id of the target .NET process. Optional — when omitted, the " +
        "server auto-selects the lone visible .NET process (bootstrap implícito, #42). Pass an " +
        "explicit pid when multiple processes are reachable.";

    [McpServerPrompt(Name = "diagnose-high-latency", Title = "Diagnose high latency / slow app")]
    [Description(
        "Step-by-step CPU/latency investigation for a running .NET process. Starts from a counter " +
        "baseline, branches into CPU sampling, thread-pool contention, and exception storms based on what the data shows.")]
    public static PromptMessage DiagnoseHighLatency(
        [Description(ProcessIdParamDescription)] int? processId = null,
        [Description("Collection window (seconds) for each sampling step. Defaults to 10.")] int? durationSeconds = null,
        [Description("Optional human-readable hint about the symptom (e.g. 'p99 latency doubled after 14:00').")] string? symptom = null)
    {
        var pid = PidArg(processId);
        var dur = durationSeconds is > 0 ? durationSeconds.Value : 10;
        var ctx = string.IsNullOrWhiteSpace(symptom) ? "." : $" — context: {symptom}.";
        return AssistantPrompt($$"""
            Goal: explain why the target .NET process is slow{{ctx}}

            Hypothesis tree: CPU bound → GC bound → I/O / downstream bound → thread-pool contention.

            Execute the plan below by calling tools on THIS MCP server. Each tool already accepts
            an optional `processId` ({{pid.HintNote}}); read every response's `summary` + `hints`
            and follow the first hint unless a previous step already disproved it.

              1. `collect_events(kind="counters", {{pid.Arg}}durationSeconds={{dur}})` — cheap first signal. Read
                 `cpu-usage`, `threadpool-queue-length`, `monitor-lock-contention-count`,
                 `gc-heap-size`, `time-in-gc`, `exception-count`, and (if AspNetCore is loaded)
                 `request-duration` + `requests-per-second`. The response also stamps a
                 `resolvedProcess` envelope confirming runtime flavor; use it instead of a
                 separate `inspect_process(view="capabilities")` round-trip unless capabilities are
                 unclear.

              2. Branch on what's elevated:
                 - `cpu-usage` >= 70% on one or two cores → `collect_sample(kind="cpu", {{pid.Arg}}durationSeconds={{dur}}, topN=20)`.
                   Report the top 5 inclusive hotspots. If `resolvedProcess.runtime` is
                   `NativeAot`, skip — CPU sampling is unsupported there; substitute
                   `collect_events(kind="event_source", providerName="System.Threading", {{pid.Arg}}durationSeconds={{dur}})`
                   and surface that as a known gap.
                 - `time-in-gc` > 20% or `gc-heap-size` climbing → jump to the `diagnose-memory-growth` prompt.
                 - `monitor-lock-contention-count` rising OR `threadpool-queue-length` > 0 sustained →
                   `collect_events(kind="event_source", providerName="System.Threading.Tasks.TplEventSource", {{pid.Arg}}durationSeconds={{dur}}, maxEvents=500)`.
                 - High request duration but low CPU → likely downstream. `collect_events(kind="event_source", providerName="System.Net.Http", {{pid.Arg}}durationSeconds={{dur}}, maxEvents=300)`
                   to time outbound calls, then cross-reference with `Microsoft.AspNetCore.Hosting` for in-pipeline latency.
                 - `exception-count` climbing → jump to the `diagnose-5xx-errors` prompt.

              3. Synthesize: name the suspected hot path (method/area), the evidence, and propose
                 either a code-level fix or a follow-up collection (e.g. `collect_process_dump` if
                 root cause is unclear AND the symptom can reproduce).

            Hard rules:
              - Never call `collect_process_dump` unless steps 1–2 failed to narrow the cause OR the user explicitly asks for one.
              - Always pass `durationSeconds` <= 30. Re-run with a longer window only if the first attempt returned empty data.
              - If any step returns a structured error (envelope `error.Kind`), follow its `hints` before continuing.
            """);
    }

    [McpServerPrompt(Name = "diagnose-memory-growth", Title = "Diagnose memory growth")]
    [Description(
        "GC and allocation investigation for a process whose working set or managed heap keeps climbing. " +
        "Bounds dump cost by starting from counters + GC events and only resorting to a heap dump when justified.")]
    public static PromptMessage DiagnoseMemoryGrowth(
        [Description(ProcessIdParamDescription)] int? processId = null,
        [Description("Counters/GC observation window (seconds). Defaults to 15.")] int? windowSeconds = null,
        [Description("Optional human-readable hint (e.g. 'heap grows 20MB/min after first request').")] string? symptom = null)
    {
        var pid = PidArg(processId);
        var win = windowSeconds is > 0 ? windowSeconds.Value : 15;
        var ctx = string.IsNullOrWhiteSpace(symptom) ? "." : $" — context: {symptom}.";
        return AssistantPrompt($$"""
            Goal: explain memory growth in the target .NET process{{ctx}}

              1. `collect_events(kind="counters", {{pid.Arg}}durationSeconds={{win}})` — read `gc-heap-size`,
                 `gen-0-size`, `gen-1-size`, `gen-2-size`, `loh-size`, `poh-size`,
                 `gc-fragmentation`, and `working-set`. The response stamps `resolvedProcess`
                 with `runtime` + `canCollectGcDump`; note NativeAOT cannot do gcdump.
                 Decide:
                   - Gen2 grows monotonically → leak or large cache; continue.
                   - LOH grows → large-object allocations; continue.
                   - Working set grows but managed heap stable → native/unmanaged growth
                     (mention dotnet-monitor or a native profiler as out-of-scope).

              2. `collect_events(kind="gc", {{pid.Arg}}durationSeconds={{win}}, maxEvents=200)` — inspect
                 `data.maxPauseTime` and `data.events`. Frequent gen-2 collections with no
                 shrink = retention. If `resolvedProcess.runtime` is `NativeAot` skip this step
                 in favor of `collect_events(kind="event_source", providerName="Microsoft-Windows-DotNETRuntime", {{pid.Arg}}durationSeconds={{win}})`
                 filtered to GC keywords (NativeAOT exposes the same provider).

              3. If retention is suspected, call `inspect_heap({{pid.Arg}}source="live")` first — it is
                 much cheaper than a full dump and surfaces top-N retainers inline plus a
                 `HeapSnapshotHandle` for `query_snapshot(handle, view="…")` drill-downs.

              4. Only if `inspect_heap(source="live")` is inconclusive or unavailable, escalate to
                 `collect_process_dump({{pid.Arg}}dumpType="WithHeap")`. Report the resulting file
                 path so the user can open it in `dotnet-dump analyze`.

              5. Synthesize: leaking type (if known), suggested next analysis (`!dumpheap -stat`,
                 `!gcroot`), or a recommended code area to audit.

            Hard rules:
              - Only one `WithHeap`/`Full` dump per investigation unless asked otherwise — they're expensive.
              - Prefer `inspect_heap(source="live")` over a dump whenever the process is still healthy enough to be paused for ~1s.
            """);
    }

    [McpServerPrompt(Name = "diagnose-5xx-errors", Title = "Diagnose 5xx / exception storm")]
    [Description(
        "Investigation for a process where `exception-count` is spiking or first-chance exceptions " +
        "are suspected to be driving latency/CPU or HTTP 5xxs in production.")]
    public static PromptMessage Diagnose5xxErrors(
        [Description(ProcessIdParamDescription)] int? processId = null,
        [Description("Optional human-readable hint (e.g. 'errors spike when endpoint /api/orders is called').")] string? symptom = null)
    {
        var pid = PidArg(processId);
        var ctx = string.IsNullOrWhiteSpace(symptom) ? "." : $" — context: {symptom}.";
        return AssistantPrompt($$"""
            Goal: identify what's throwing in the target .NET process and whether it correlates with HTTP 5xxs{{ctx}}

              1. `collect_events(kind="counters", {{pid.Arg}}durationSeconds=10)` — confirm `exception-count` is
                 actually climbing and capture the rate. If it's flat, the 5xxs may be returned
                 deliberately by application code; pivot to `collect_events(kind="event_source", providerName="Microsoft.AspNetCore.Hosting", {{pid.Arg}}durationSeconds=15)`.

              2. **Start the collector BEFORE the workload that triggers the storm** — exceptions
                 thrown before the session starts are invisible. `collect_events(kind="exceptions", {{pid.Arg}}durationSeconds=15, maxRecent=30)` —
                 read `data.byType` for the dominant exception type(s) and `data.recent` for
                 stack frames.

              3. For first-chance vs unhandled differentiation, also call
                 `collect_events(kind="event_source", providerName="Microsoft-Extensions-Logging", {{pid.Arg}}durationSeconds=15, maxEvents=200)`.
                 This catches structured log entries the app considers "handled" so you can
                 correlate.

              4. If exceptions correlate with HTTP traffic, also call
                 `collect_events(kind="event_source", providerName="System.Net.Http", {{pid.Arg}}durationSeconds=10, maxEvents=200)`
                 and join the activities with the exception timestamps.

              5. Conclude:
                 - If a single type dominates AND its message points at control-flow use (e.g.
                   `KeyNotFoundException`, `FormatException` from `int.Parse`), recommend
                   replacing with `TryGet*`/`TryParse`.
                 - If exceptions are wrapped/rethrown across boundaries, suggest enabling
                   first-chance logging in the offending module rather than another collection
                   run.
                 - Only if step 2 returned zero exceptions despite a non-zero `exception-count`
                   (lost events / sampler limit), escalate to `collect_process_dump({{pid.Arg}}dumpType="Mini")`.
            """);
    }

    [McpServerPrompt(Name = "diagnose-slow-outbound-http", Title = "Diagnose slow outbound HTTP calls")]
    [Description(
        "Investigation for a process whose own request handling is slow but CPU is low — typically the latency lives in a downstream HTTP dependency.")]
    public static PromptMessage DiagnoseSlowOutboundHttp(
        [Description(ProcessIdParamDescription)] int? processId = null,
        [Description("Collection window (seconds). Defaults to 30 so request/response pairs have time to complete.")] int? durationSeconds = null,
        [Description("Optional human-readable hint (e.g. 'POST /checkout is slow only after 14:00').")] string? symptom = null)
    {
        var pid = PidArg(processId);
        var dur = durationSeconds is > 0 ? durationSeconds.Value : 30;
        var ctx = string.IsNullOrWhiteSpace(symptom) ? "." : $" — context: {symptom}.";
        return AssistantPrompt($$"""
            Goal: confirm that latency in the target .NET process is dominated by outbound HTTP (not CPU, not GC){{ctx}}

              1. `collect_events(kind="counters", {{pid.Arg}}durationSeconds=10)` — verify `cpu-usage` is low,
                 `time-in-gc` is normal, and (if AspNetCore is present) `request-duration` is
                 high. If CPU or GC is elevated, pivot to `diagnose-high-latency` instead.

              2. `collect_events(kind="event_source", providerName="System.Net.Http", {{pid.Arg}}durationSeconds={{dur}}, maxEvents=500)` —
                 look at `events` for `RequestStart` / `RequestStop` pairs. Most clients emit
                 timing on the stop event payload. Group by URI/host and report the p95 latency
                 per destination.

              3. Cross-reference with `collect_events(kind="event_source", providerName="Microsoft-AspNetCore-Server-Kestrel", {{pid.Arg}}durationSeconds={{dur}}, maxEvents=300)`
                 for inbound connection lifecycle. This confirms the latency is downstream-induced
                 (inbound queue stable) rather than client-induced.

              4. If `RequestStart` is followed by long gaps before `RequestStop`, you've isolated
                 it to the downstream dependency. If `RequestStart` itself is delayed, suspect
                 thread-pool starvation — pivot to `diagnose-high-latency` step 2's TPL branch.

              5. Synthesize: name the slow destination(s), the p95/p99, and recommend a
                 client-side timeout/retry/circuit-breaker review (and a downstream owner ping).
            """);
    }

    [McpServerPrompt(Name = "triage-nativeaot", Title = "Triage a NativeAOT app's diagnostic capabilities")]
    [Description(
        "Fast 'what can I even collect from this process?' triage when you suspect the target is NativeAOT-published or has EventSource support disabled.")]
    public static PromptMessage TriageNativeAot(
        [Description(ProcessIdParamDescription)] int? processId = null)
    {
        var pid = PidArg(processId);
        return AssistantPrompt($$"""
            Goal: determine whether the target is NativeAOT and which collectors it supports, so subsequent investigations don't waste cycles on unsupported tools.

              1. `inspect_process({{pid.Arg}}view="capabilities")` — check
                 `runtime` (CoreClr vs NativeAot), `runtimeVersion`, and the capability flags.
                 Available flags: `canReadEventCounters`, `canSampleCpu`, `canCollectGcDump`,
                 `canCollectExceptions`, `canCollectHttpActivity`, `canCollectCustomEventSource`,
                 `canCollectProcessDump`.

              2. Interpret:
                 - `runtime` = `NativeAot` and `canReadEventCounters` = `false`: the app was
                   published with `<EventSourceSupport>false</EventSourceSupport>`. NO live
                   collectors will work. Only path forward is `collect_process_dump` +
                   `inspect_heap(source="dump")` offline OR rebuild with EventSource support enabled.
                 - `runtime` = `NativeAot` and `canReadEventCounters` = `true`: counters,
                   exceptions, GC events, custom EventSources, dumps all work. `canSampleCpu` is
                   `false` (SampleProfiler is CoreCLR-only) — fall back to `perf record -p <pid>`
                   on the host for CPU sampling, or use `collect_events(kind="event_source")` on the runtime
                   provider for coarse signal.
                 - `runtime` = `CoreClr`: all collectors are supported; no special handling.

              3. Report back: a one-line "this is a NativeAOT/CoreCLR app with X/Y collectors
                 available" plus, if NativeAOT, a recommended tool list for the next step
                 (counters → exceptions/GC events → dump).
            """);
    }

    [McpServerPrompt(Name = "diagnose-safely-in-prod", Title = "Diagnose safely in production")]
    [Description(
        "Choose the cheapest, least-disruptive collection that can still answer the user's question on a live production process.")]
    public static PromptMessage DiagnoseSafelyInProd(
        [Description(ProcessIdParamDescription)] int? processId = null)
    {
        var pid = PidArg(processId);
        return AssistantPrompt($$"""
            Goal: investigate the target .NET process in production with the smallest possible blast radius.

            Order of escalation from cheapest to most disruptive (stop at the first level that answers the question):

              1. **Passive metadata** — `inspect_process({{pid.Arg}}view="info")` and
                 `inspect_process({{pid.Arg}}view="capabilities")`. No EventPipe session, no pause.
              2. **`collect_events(kind="counters", {{pid.Arg}}durationSeconds=10)`** — small EventPipe session,
                 low overhead, sufficient for most "is anything elevated?" questions.
              3. **`collect_events` / `collect_sample`** —
                 EventPipe sessions sized by `durationSeconds`. Keep ≤ 15s in prod; cap
                 `maxEvents` and `maxRecent` to bound payload size.
              4. **`collect_sample(kind="cpu")`** — same family as 3 but uses SampleProfiler at ~1 kHz.
                 Higher overhead than counters; still safe at ≤ 10s windows.
              5. **`inspect_heap(source="live")`** — pauses the process briefly (~1s on heaps ≤ 1 GB) to
                 enumerate types. Strongly prefer this over a `WithHeap` dump when the question
                 is "what's retained?".
              6. **`collect_process_dump(dumpType="Mini")`** — kernel reads memory; briefly pauses
                 the process. Acceptable in prod for post-mortem on a non-critical instance.
              7. **`collect_process_dump(dumpType="WithHeap")` or `"Full"`** — can pause for
                 seconds and writes hundreds of MB. NEVER call this in prod without explicit
                 human approval; prefer doing it on a drained replica.

            Hard rules:
              - In hot-path scenarios, always prefer windowed EventPipe tools (steps 2–4) over dumps (steps 6–7).
              - Always pass the shortest `durationSeconds` that produces a non-empty answer. If the response is empty, increase the window — don't immediately escalate to a dump.
              - Never call `collect_process_dump` with `dumpType="Full"` without explicit human approval.
              - Tools are read-only except `collect_process_dump`, which is marked Destructive.
            """);
    }

    // --- helpers ---------------------------------------------------------------------------

    private readonly record struct PidArgs(string Arg, string HintNote);

    /// <summary>
    /// Builds the textual fragment used to interpolate <c>processId</c> into a tool-call example.
    /// When the caller passes a pid, we emit <c>processId=1234, </c>. When they don't, we emit an
    /// empty string and a short hint so the prompt body documents both shapes — auto-resolution
    /// (#42) means the tool will pick the lone reachable .NET process by itself.
    /// </summary>
    private static PidArgs PidArg(int? processId)
    {
        if (processId is > 0)
        {
            return new PidArgs(
                Arg: $"processId={processId.Value}, ",
                HintNote: $"caller supplied processId={processId.Value}");
        }

        return new PidArgs(
            Arg: string.Empty,
            HintNote: "omit processId to let the server auto-select the lone .NET process — see #42 / `resolvedProcess` envelope");
    }

    /// <summary>
    /// Wraps the rendered playbook in a single <see cref="Role.User"/> message whose content is
    /// annotated with <c>audience: [Assistant]</c>. MCP clients that distinguish user-facing
    /// templates from assistant-facing context (Claude Desktop, Cursor, Copilot CLI) use this
    /// annotation to route the prompt directly into the LLM's context window instead of into the
    /// chat UI as a user message preview.
    /// </summary>
    private static PromptMessage AssistantPrompt(string body) =>
        new()
        {
            Role = Role.User,
            Content = new TextContentBlock
            {
                Text = body,
                Annotations = new Annotations
                {
                    Audience = new[] { Role.Assistant },
                },
            },
        };
}
