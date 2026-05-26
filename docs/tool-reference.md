# Tool reference

Every tool exposed by `dotnet-diagnostics-mcp` is listed here with its purpose, parameters,
return shape, runtime requirements, and a sample invocation. All tools are
delivered over Streamable HTTP at `POST /mcp` and require an
`Authorization: Bearer <token>` header (see [client-setup.md](./client-setup.md)).

> Return shapes link back to the C# record definitions in
> [`src/DotnetDiagnosticsMcp.Core`](../src/DotnetDiagnosticsMcp.Core), which are the source of
> truth for field names and types.

### Bootstrap implícito (`processId` is optional)

Since issue #42 every tool that targets a live .NET process accepts `processId`
as optional. When the caller omits it the server lists the visible .NET
processes via the diagnostic IPC and:

- **0 candidates** → structured error `NoDotnetProcessFound`.
- **1 candidate** → auto-selects it, marks the response's
  `resolvedProcess.autoResolved = true`.
- **N candidates** → structured error `AmbiguousDotnetProcess` with the
  candidate list inline; re-issue the call with `processId` set explicitly.

Every successful response now carries a `resolvedProcess` digest on the
envelope alongside `data` / `summary` / `hints`:

```json
{
  "resolvedProcess": {
    "processId": 1234,
    "runtime": "CoreClr",
    "runtimeVersion": "10.0.0",
    "canSampleCpu": true,
    "canCollectGcDump": true,
    "autoResolved": true
  }
}
```

This means the previously-obligatory opener of
[`inspect_process(view="list")`](#inspect_process) → `inspect_process(view="capabilities")` → `<tool>` collapses to
a single `<tool>` call when there is only one .NET process visible to the
sidecar. The capability digest is cached per pid for 60 seconds so back-to-back
tool calls within an investigation pay the probe cost once.

### Verbosidade (`depth`)

Issue [#41 slice 2c](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/issues/41)
adds a uniform `depth` parameter to every windowed collector. Values:
`Summary` (default), `Detail`, `Raw`. Contract:

- `Summary` returns a small, decision-grade payload inline (the smallest piece
  of evidence the LLM needs to choose the next tool). This is the default.
- `Detail` returns the historical pre-#41 payload (top-N hotspots, full
  `Events[]` lists, full `Notes`, etc.).
- `Raw` is reserved for parity with the artifact handle; today equivalent to
  `Detail` for every tool.

**Key invariant — the handle store always carries the FULL artifact**, regardless
of `depth`. The depth knob only filters the *inline* response. Drilldown is now
unified behind a single verb — **[`query_snapshot(handle, view, …)`](#query_snapshot)**
(RFC 0002 §4.1 / #207) — which dispatches on the handle's recorded artifact kind
and re-projects everything the original collection captured. The five legacy
drilldown tools (`query_snapshot`, `query_snapshot`,
`query_snapshot`, `query_snapshot`, `query_snapshot(view="call-tree")`) remain registered
through the 0.9.0 deprecation window as byte-equal aliases of the unified verb.

Per-tool `Summary` semantics:

| Tool | What `Summary` drops inline |
| --- | --- |
| `collect_events(kind="counters")` | All non-headline counters (keeps ~12: cpu-usage, working-set, gc-heap-size, gen-2-gc-count, threadpool-thread-count, threadpool-queue-length, exception-count, monitor-lock-contention-count + ASP.NET Core requests/failed/current + Kestrel connections-per-sec). |
| `inspect_process(view="container")` | The `Notes[]` (caveats about cgroup v1 / missing PSI). Cgroup values themselves remain. |
| `collect_sample(kind="cpu")` | `TopHotspots` truncated to the top 3 (handle keeps `topN`, default 25). |
| `collect_sample(kind="off_cpu")` | `TopBlockingStacks` truncated to the top 3 (handle keeps `topN`). |
| `collect_events(kind="exceptions")` | The `Recent[]` list. `Total` and `ByType` remain exact (counts at every depth). |
| `collect_events(kind="gc")` | The `Events[]` list. Totals, max pause, per-gen counts remain exact. |
| `collect_events(kind="event_source")` | The `Events[]` list. Provider + total count remain. Drill in with `query_snapshot(handle, view=byEventName)`. |
| `collect_events(kind="logs")` | The `Recent[]` list. Level counts + per-category rollups remain exact for the window. |
| `collect_events(kind="jit")` | Method rows beyond the hottest 10. Healthcheck + tier counts remain exact for the window. |
| `collect_events(kind="threadpool")` | The full worker/IOCP timelines and hill-climbing sequence. Summary keeps headline counts + top origins; drill in with `query_snapshot(handle, view=timeline|hillClimbing|workItemOrigins)`. |
| `collect_events(kind="db")` | The long `ByCommand[]` / `NPlusOne[]` lists. Summary keeps the headline aggregates + pool slice. |
| `collect_thread_snapshot` | The lock graph + threads beyond the top 3 most-blocked. Drill in with `query_snapshot(view=lock-graph|deadlocks|unique-stacks)`. |

Explicit `topN` always wins over the depth default — if you pass
`topN=10, depth=Summary` you get up to 10 hotspots inline (the LLM knows what
it asked for).

`collect_events(kind="activities")` does **not** currently expose `depth`; it always returns the
retained `Activities[]` inline (bounded by `maxActivities`) and relies on
`query_snapshot(handle, view=...)` for narrower drilldown views.

### Long-running collects: MCP Tasks

As of the `2025-11-25` protocol bump, the server registers an
`IMcpTaskStore`, advertises `capabilities.tasks.{list,cancel,requests.tools.call}`
and marks these tools with `execution.taskSupport: "optional"` in `tools/list`:

- `collect_sample(kind="cpu")`
- `collect_events(kind="exceptions")`
- `collect_events(kind="gc")`

**Spec-compliant clients should use MCP Tasks** for long windows:

1. send `tools/call` with `params.task` (or use `McpClient.CallToolAsTaskAsync`)
2. poll `tasks/get`
3. fetch the terminal `CallToolResult` via `tasks/result`
4. cancel via `tasks/cancel`

### MCP-native progress and cancellation (RFC 0002 §7.3 #7 / issue #211)

In addition to MCP Tasks, long-running collectors emit standard MCP
`notifications/progress` messages and honor `notifications/cancelled` on the
same `tools/call` request — no second round-trip, no polling. This is the
**preferred** path for clients that don't implement the full Tasks lifecycle.

Tools wired up:

- `collect_sample(kind="cpu")`
- `collect_events` (every `kind` — counters, exceptions, gc, event_source, activities)

How it works:

- The client sends a normal `tools/call` request with `_meta.progressToken` set
  (most C# / TypeScript SDKs do this automatically when an `IProgress<…>` is passed
  to `CallToolAsync`).
- The server emits `notifications/progress` on a ~1s cadence while the collector
  is running, plus a terminal `progress=100` on success.
- If the client cancels the in-flight `tools/call` request (its SDK
  `CancellationToken` trips, or it sends an MCP `notifications/cancelled`
  scoped to that **request id** — not to the progress token), the underlying
  EventPipe / sampler session is torn down and the server returns a
  `DiagnosticResult<T>` envelope with `cancelled: true` and empty data.
  Depending on which side of the race wins, some MCP client SDKs surface
  the cancellation as an `OperationCanceledException` instead of returning
  the envelope — both shapes are spec-conformant.

> **Removed in Stage B (RFC 0002 §7.3 #7 / issue #211).** The legacy polling
> bridge — `collect_sample(kind="cpu")(runAsJob=true)`, `get_collection_status(handle)`,
> `cancel_collection(handle)` — has been removed. Clients must use MCP Tasks or
> the in-request progress/cancel notifications described above.

### Prompts (curated playbooks)

In addition to tools, the server exposes 6 MCP **Prompts** that pre-package the
investigation strategies from [`investigation-playbooks.md`](./investigation-playbooks.md)
so the LLM can opt into a baked recipe instead of re-planning the next call
after every step. Prompts do not consume the tool-slot budget — clients
discover them via `prompts/list` and request a specific one via `prompts/get`.

| Prompt | Source playbook | Required inputs |
|---|---|---|
| `diagnose-high-latency` | "The app feels slow / high latency" | none (all optional: `processId?`, `durationSeconds?`, `symptom?`) |
| `diagnose-memory-growth` | "Memory keeps growing" | none (`processId?`, `windowSeconds?`, `symptom?`) |
| `diagnose-5xx-errors` | "We're seeing 5xxs in production" | none (`processId?`, `symptom?`) |
| `diagnose-slow-outbound-http` | "Slow outbound HTTP calls" | none (`processId?`, `durationSeconds?`, `symptom?`) |
| `triage-nativeaot` | "Is this a NativeAOT app?" | none (`processId?`) |
| `diagnose-safely-in-prod` | "Safest investigation in production" | none (`processId?`) |

Every prompt returns a single `user`-role message whose content is annotated
with `audience: ["assistant"]` so MCP clients that distinguish user-facing
templates from assistant-facing context route them directly into the LLM's
context window. Each prompt embeds the hypothesis tree from the playbook plus
exact tool-call examples (with placeholder args reflecting bootstrap implícito).
The LLM may always ignore a prompt and drive ad-hoc.

### Handle chaining nos coletores (`query_snapshot`)

Os 7 coletores windowed — `collect_events(kind="counters")`, `collect_events(kind="exceptions")`,
`collect_events(kind="gc")`, `collect_events(kind="activities")`, `collect_events(kind="event_source")`, `collect_events(kind="logs")`, `collect_events(kind="jit")` — devolvem, junto do summary +
Os 6 coletores windowed — `collect_events(kind="counters")`, `collect_events(kind="exceptions")`,
`collect_events(kind="gc")`, `collect_events(kind="activities")`, `collect_events(kind="event_source")`, `collect_events(kind="logs")`, `collect_events(kind="threadpool")` — devolvem, junto do summary +
`collect_events(kind="gc")`, `collect_events(kind="activities")`, `collect_events(kind="event_source")`, `collect_events(kind="logs")`, `collect_events(kind="db")` — devolvem, junto do summary +
top-N inline, um `handle` opaco (Crockford-base32, TTL ~10 min) registrado num
store em memória. A LLM pode então re-projetar o mesmo artefato sob outra
visão **sem rodar o EventPipe de novo** chamando `query_snapshot`:

```jsonc
// 1. coleta uma vez
collect_events(kind="exceptions")(processId=4242, durationSeconds=10)
  → { summary: "30 exceptions (3 types)", handle: "01H...XY", data: { … top-N } }

// 2. drilldown N vezes dentro da janela TTL
query_snapshot(handle="01H...XY", view="recent", topN=20)
query_snapshot(handle="01H...XY", view="byType")
```

`query_snapshot` (RFC 0002 §4.1 / #207) é o verbo único de drilldown — ele
faz dispatch pelo `kind` que o handle carrega e cobre os 11 kinds emitidos
pelos coletores acima + heap (`heap-snapshot`), thread (`thread-snapshot`),
off-CPU (`off-cpu-snapshot`) e call-tree (`cpu-sample` / `allocation-sample`).
Os 5 verbos legados (`query_snapshot`, `query_snapshot`,
`query_snapshot`, `query_snapshot`, `query_snapshot(view="call-tree")`) seguem
registrados como aliases byte-equal durante a janela de depreciação 0.9.0
(asserted por `QuerySnapshotCompatibilityTests`).

Visões disponíveis por `kind`:

| Kind | Emitido por | Views aceitas |
|---|---|---|
| `counters` | `collect_events(kind="counters")` | `summary` (default), `byProvider` |
| `exception-snapshot` | `collect_events(kind="exceptions")` | `summary` (default = `byType.Take(topN)`), `byType`, `recent` |
| `gc-events` | `collect_events(kind="gc")` | `summary` (default), `events`, `pauseHistogram` |
| `activities` | `collect_events(kind="activities")` | `summary` (default), `bySource`, `byOperation`, `activities` |
| `event-source` | `collect_events(kind="event_source")` | `summary` (default), `byEventName`, `events` |
| `log-snapshot` | `collect_events(kind="logs")` | `summary` (default), `byCategory`, `byLevel`, `recent`, `errors` |
| `jit-snapshot` | `collect_events(kind="jit")` | `summary` (default), `topMethods`, `tierDistribution`, `reJIT` |
| `threadpool-snapshot` | `collect_events(kind="threadpool")` | `summary` (default), `timeline`, `hillClimbing`, `workItemOrigins` |
| `db-snapshot` | `collect_events(kind="db")` | `summary` (default), `byCommand`, `n+1`, `connectionPool` |
| `heap-snapshot` | `inspect_heap` / `inspect_heap(source="live")` / `inspect_heap(source="dump")` | `top-types` (default), `retention-paths`, `roots-by-kind`, `finalizer-queue`, `fragmentation`, `static-fields`, `delegate-targets`, `duplicate-strings`, `object`, `gcroot`, `objsize`, `async`, `diff` |
| `thread-snapshot` | `collect_thread_snapshot` | `top-blocked` (default), `threads-summary`, `stack`, `lock-graph`, `deadlocks`, `unique-stacks`, `threadpool` |
| `off-cpu-snapshot` | `collect_sample(kind="off_cpu")` | `topStacks` (default), `byThread`, `stack` |
| `cpu-sample` / `allocation-sample` | `collect_sample(kind="cpu")` / `collect_sample(kind="allocation")` | `call-tree`, `diff` |

Autorização é aplicada por kind no dispatcher (`heap-read` para heap,
`ptrace` para thread, `eventpipe` para off-CPU, `investigation-export` para
cpu/allocation call-tree + diff, `heap-read` para heap diff, `read-counters`|`eventpipe` para collection) — o gate estático aceita
qualquer um dos 5 escopos para o tool surface; o boundary por kind preserva o
contrato de cada legado verbatim (RFC 0002 §4.1).

`view="diff"` accepts `baselineHandle` (required), `minDeltaPct` (default `5.0`) and `topN`
(default `25`). Accepted pairs are `cpu-sample × cpu-sample`, `heap-snapshot × heap-snapshot`
and `allocation-sample × allocation-sample`. Allocation diffs normalize totals to per-second
rates when the two capture windows use different durations and surface both raw + normalized
metrics in each row.

> **Nota — truncação em `event-source`:** o coletor para de armazenar eventos
> ao atingir `maxEvents`, mas continua contando o total. As views
> `summary`/`byEventName` agora trazem `capturedCount` e `truncated`; quando
> `truncated=true` os grupos refletem só o prefixo capturado — re-rode
> `collect_events(kind="event_source")` com `maxEvents` maior pra agregados exatos.

Handles invalidam quando: o TTL expira, o processo alvo morre (evicção
automática), ou um restart do server zera o store. Acesso a handle
desconhecido devolve `DiagnosticError { Kind: "HandleExpired" }` com um
`NextActionHint` apontando o coletor original.

Esse contrato é o equivalente "split collector, unified drilldown"
(documentado em [`AGENTS.md`](../AGENTS.md)) aplicado a *todos* os coletores
— mesmo padrão de `inspect_heap(source="dump")`/`inspect_heap(source="live")` e
`collect_thread_snapshot`, agora colapsado num único verbo de query.

### Kernel-side signals (`inspect_process(view="container")`)

Mata o blind-spot mais comum em K8s: "app está lento, mas EventCounters dizem
que CPU/memória estão ok" — na maior parte das vezes é **CPU throttling no
cgroup**, invisível pelo runtime. `inspect_process(view="container")` lê cgroup v2 +
`/proc/<pid>/oom_score` e devolve:

- `Cpu`: `usage_usec`, `nr_periods`, `nr_throttled`, `throttled_usec`,
  `ThrottlePercent` (canonical signal) e `QuotaCores` (null = unlimited).
- `Memory`: `current`, `max`, `high`, `UsageFraction`, contadores
  `oom_kill` / `max-hit` extraídos de `memory.events`.
- `Pressure` (PSI): `cpu.some.avg10`, `memory.some/full.avg10`, `io.some/full.avg10`.
- `Pids` e `oom_score`.

Tudo best-effort: arquivos faltando (PSI em kernel antigo, sem limite de
memória, container sem read em `memory.events`) viram entradas em `Notes`, não
erro fatal. Em Windows / cgroup v1 / sem cgroup, devolve `InContainer=false`
+ `CgroupVersion` correto e `Notes` explicativo (job-object metrics ainda não
foram wired).

O `inspect_process(view="capabilities")` ganhou as flags do kernel-side para você saber
se vale a pena tentar a coleta antes: `InContainer`, `CgroupV2`,
`CanSeeThrottle` (true sse há quota configurada → throttling é observável),
`PsiAvailable`, `PerfInstalled`, `HasCapPerfmon`, `PerfEventParanoid`,
`HasCapSysPtrace`, `PtraceScope` e `EtwKernelOk`. Slice 2b também expõe
**`CanSampleOffCpu`** — true quando o sidecar já cumpre os pré-requisitos do
backend (Linux: perf + privilégio suficiente para `sched_switch`; Windows:
processo elevado). Quando false, `Notes` traz a hint concreta do motivo antes
da LLM tentar `collect_sample(kind="off_cpu")` num sidecar sem privilégio.

NextActionHints: throttle > 5% sugere `collect_sample(kind="cpu")` direto; memória >
85% do limite sugere `inspect_heap(source="live")` antes do OOM-kill.

### Off-CPU sampling (`collect_sample(kind="off_cpu")` + `query_snapshot`)

> **DEPRECATED (0.9.0).** Call [`collect_sample(kind="off_cpu", …)`](#collect_sample) instead; the legacy `collect_sample(kind="off_cpu")` remains registered behind a deprecation banner during the window (RFC 0002 §4.2 / issue #210).

Complementa o `collect_sample(kind="cpu")` (que mostra **on-CPU** — onde o app gasta
tempo executando) com **off-CPU** — onde threads ficaram **bloqueadas**
(I/O, locks, condvars, monitor wait). Resolve o blind-spot clássico "CPU baixa
mas latência alta": sampling on-CPU não enxerga porque as threads não estão
rodando.

- **Linux:** usa `perf record -a -e sched:sched_switch --call-graph dwarf` em
  todo o sistema (o tracepoint `sched_switch` só dispara na thread que sai de
  CPU, então restringir por PID perde o evento de IN). Spans são filtrados
  pós-coleta pelo `/proc/<pid>/task/*` do alvo. Requer `CAP_PERFMON` (kernel
  ≥ 5.8) ou `perf_event_paranoid <= -1`, e `perf` instalado
  (`linux-tools-common` / `linux-tools-$(uname -r)` no Debian/Ubuntu).
  `SymbolSource: "perf-sched-dwarf"`.
- **Windows:** usa a sessão NT Kernel Logger via `TraceEvent` com
  `ContextSwitch + Dispatcher + ImageLoad/Process/Thread`, stack walk no
  `ContextSwitch` (a stack capturada na hora do switch-out é exatamente a
  chamada bloqueante). Wait reason do kernel
  (`UserRequest` / `WrLpcReceive` / `WrQueue`...) vira o `PrevState` do span,
  mirror direto do `S/D/I` do Linux. Spans pendentes ao fim da janela viram
  censored (`IsCensored=true`) com duração lower-bound, igual ao Linux.
  Requer **BUILTIN\\Administrators** ou `SeSystemProfilePrivilege`; sem isso
  devolve `PermissionDenied` com hint apontando os dois caminhos suportados
  (`Administrators` **ou** `Profile system performance`). Pra produção, ver
  [`windows-sidecar-service.md`](./windows-sidecar-service.md)
  (Windows Service com `LocalSystem` ou conta dedicada + privilégio único).
  `SymbolSource: "etw-cswitch-pdb"` (resolve PDBs locais + `_NT_SYMBOL_PATH`).
- **Managed↔kernel stack merge:** ainda não — frames são puramente nativos /
  kernel em ambas as plataformas. Sub-slice 2c.

`collect_sample(kind="off_cpu")(pid, durationSeconds=10, topN=10)` devolve `{handle,
summary, top}` com os stacks que mais tempo passaram off-CPU.
`query_snapshot(handle, view, ...)` segue o padrão **split collector,
unified drilldown**: `view="topStacks"` (default), `view="byThread"`
(agregado por TID com `TopBlockingLeaf` + estado dominante), ou
`view="stack"` com `stackRank=N` (1-based) pra exportar o stack completo.


## Quick index

> NativeAOT coverage detail (which symbol source per tool, per OS): see
> [`aot-coverage.md`](./aot-coverage.md).

| Tool | Cost | Requires CoreCLR? | NativeAOT? | Side effects |
|---|---|---|---|---|
| [`inspect_process`](#inspect_process) | depends on `view` | no | ✅ | union of the five legacy bootstrap tools below |
| [`inspect_process(view="list")`](#inspect_process(view="list")) *(deprecated — use `inspect_process(view="list")`)* | cheap | no | ✅ | none |
| [`inspect_process(view="info")`](#inspect_process(view="info")) *(deprecated — use `inspect_process(view="info")`)* | cheap | no | ✅ | none |
| [`inspect_process(view="capabilities")`](#inspect_process(view="capabilities")) *(deprecated — use `inspect_process(view="capabilities")`)* | ~2 s | no | ✅ | opens a short EventPipe probe |
| [`inspect_process(view="container")`](#inspect_process(view="container")) *(deprecated — use `inspect_process(view="container")`)* | cheap | no | ✅ (Linux) | reads `/sys/fs/cgroup` + `/proc` files |
| [`inspect_process(view="memory_trend")`](#inspect_process(view="memory_trend")) *(deprecated — use `inspect_process(view="memory_trend")`)* | window-bound | no | ✅ | reads `/proc/<pid>/smaps_rollup` + `/proc/<pid>/stat` (Linux) or `GetProcessMemoryInfo` (Windows) |
| [`inspect_process(view="runtime-config")`](#inspect_process(view="runtime-config")) | cheap | no | ✅ (Windows env partial) | ClrMD GC / ThreadPool probe + filtered `/proc/<pid>/environ` (Linux) |
| [`inspect_process(view="resources")`](#inspect_process(view="resources")) | cheap / window-bound | no | ✅ (Linux/Windows partial) | reads `/proc/<pid>/fd`, `/proc/<pid>/net/tcp{,6}`, `/proc/<pid>/limits` (Linux) or `GetProcessHandleCount` (Windows) |
| [`inspect_process(view="requests-now")`](#inspect_process(view="requests-now")) | ~2 s | no | ✅ (ptrace required) | short EventPipe request window + live thread snapshot |
| `collect_sample(kind="off_cpu")` (Linux/Windows) | window-bound | no | ✅ (Linux) | **Deprecated — use `collect_sample(kind="off_cpu")`.** system-wide `perf record` (Linux) / NT Kernel Logger CSwitch (Windows, admin) |
| `query_snapshot` | cheap | no | ✅ | drilldown on handle from `collect_sample(kind="off_cpu")` |
| [`collect_events`](#collect_events) | window-bound | no | ✅ (mostly — see kind) | **Canonical EventPipe collector.** Dispatches by `kind` to counters/exceptions/gc/event_source/activities/logs. |
| [`collect_sample`](#collect_sample) | window-bound | depends on kind | ✅ (mostly — see kind) | **Canonical bounded-time sampler.** Dispatches by `kind` to cpu/off_cpu/allocation. |
| [`collect_events(kind="counters")`](#collect_events(kind="counters")) | window-bound | no | ✅ | **Deprecated — use `collect_events(kind="counters")`.** opens an EventPipe session |
| [`collect_sample(kind="cpu")`](#collect_sample(kind="cpu")) | window-bound | no | ✅ (perf/ETW, native frames) | **Deprecated — use `collect_sample(kind="cpu")`.** EventPipe + temp `.nettrace` on disk |
| [`collect_sample(kind="allocation")`](#collect_sample(kind="allocation")) | window-bound | no | ⚠️ TypeName empty | **Deprecated — use `collect_sample(kind="allocation")`.** EventPipe session |
| [`collect_events(kind="exceptions")`](#collect_events(kind="exceptions")) | window-bound | no | ✅ | **Deprecated — use `collect_events(kind="exceptions")`.** EventPipe session |
| [`collect_events(kind="gc")`](#collect_events(kind="gc")) | window-bound | no | ✅ | **Deprecated — use `collect_events(kind="gc")`.** EventPipe session |
| [`collect_events(kind="activities")`](#collect_events(kind="activities")) | window-bound | no | ✅ | **Deprecated — use `collect_events(kind="activities")`.** EventPipe session |
| [`collect_events(kind="event_source")`](#collect_events(kind="event_source")) | window-bound | no | ⚠️ provider must be embedded at publish | **Deprecated — use `collect_events(kind="event_source")`.** EventPipe session |
| `collect_thread_snapshot` / `query_snapshot` | seconds | no | ✅ via `linux-native-stack` / `etw-native-stack` | ptrace attach (Linux) / kernel logger (Windows) |
| `inspect_heap` (canonical) / `inspect_heap(source="live")` / `inspect_heap(source="dump")` (deprecated aliases, 0.7.0) / `query_snapshot` | seconds | **yes** | ❌ | ClrMD walks managed heap (heap drilldown values metadata-only by default — see [Security gates](#security-gates-b4)) |
| [`collect_process_dump`](#collect_process_dump) | seconds–minutes | no | ✅ (native dump) | **writes a dump file to disk** |
| [`capture_method_bytes`](#capture_method_bytes) | cheap | **yes** | ❌ (use `dotnet-native-mcp.disassemble`) | reads JIT code-heap |
| `get_bytes(kind="module")` | cheap | **yes** (live module attach) | ❌ (materialize locally, then hand off) | streams PE / PDB bytes over MCP chunks |
| `get_bytes(kind="dump")` | cheap | no | ❌ (materialize locally, then hand off) | streams dump bytes from `MCP_ARTIFACT_ROOT` |
| `list_orchestrator(kind=pods\|investigations)` (orchestrator) | cheap | n/a | n/a | RFC 0002 §4.7 successor to `list_orchestrator(kind="pods")` + `list_orchestrator(kind="investigations")`. `kind=pods` → Kubernetes `pods.list` (scope `orchestrator-list`); `kind=investigations` → in-memory handle snapshot (scope `orchestrator-attach`). **Opt-in**, registered only when `Orchestrator:Enabled=true`. Legacy tool names remain accepted for one deprecation window (removed in 0.7.0). |

"Window-bound" means the duration is the dominant cost; the tool will block for
~`durationSeconds`.

### Linux runtime requirements

EventPipe-based tools (including `collect_events(kind="activities")`, alongside the ones listed in the index above) only need the
diagnostic IPC socket, which works as long as the MCP server runs as the
**same UID** as the target process. ClrMD-backed tools added since the MVP —
`collect_thread_snapshot`, `inspect_heap(source="live")`, `inspect_heap(source="dump")` against a live
PID, `collect_process_dump`, and `get_bytes(kind="module")` — additionally call
`ptrace(PTRACE_ATTACH, …)` under the hood. On Linux, matching UIDs is **not**
sufficient when the host's
`kernel.yama.ptrace_scope` is `1` (the Debian/Ubuntu/WSL default): the kernel
blocks same-UID peer attach.

If a request lands in that state you'll get a structured error envelope (see
issue #32):

```json
{ "error": { "kind": "PermissionDenied",
             "message": "Could not PTRACE_ATTACH to any thread of the process N." } }
```

Mitigations:

- **Docker:** add `--cap-add SYS_PTRACE` to the **sidecar** container.
- **Kubernetes:** set `capabilities.add: ["SYS_PTRACE"]` on the sidecar
  container's `securityContext` (see [`deploy/k8s/sample-sidecar.yaml`](../deploy/k8s/sample-sidecar.yaml)).
- **Bare host / local dev:** `sudo sysctl -w kernel.yama.ptrace_scope=0`, or
  run the MCP server as root.

When ptrace cannot be granted, fall back to `collect_process_dump` +
`inspect_heap(source="dump")` (the dump capture runs in the target's own process, so it does
not require ptrace from the sidecar — but writing the dump file is still
gated on the diagnostic socket UID).

For **NativeAOT on Linux**, `collect_thread_snapshot` now routes to
`eu-stack -p <pid>` (elfutils) instead of ClrMD. The snapshot payload carries
`source: "linux-native-stack"` and maps wait reason from
`/proc/<pid>/task/<tid>/{status,wchan}` (`BlockedOnLock`, `BlockedOnIO`,
`BlockedOnUninterruptibleIO`, `Stopped`, `Running`). This path still requires
same-UID + ptrace gate; when denied the `PermissionDenied` envelope includes a
hint to the perf-replay fallback tracked in issue #92.

---

## `inspect_process`

**Canonical bootstrap tool** ([RFC 0002 §4.6](./rfcs/0002-tool-surface-consolidation.md)).
Consolidates the five legacy metadata tools — `inspect_process(view="list")`,
`inspect_process(view="info")`, `inspect_process(view="capabilities")`, `inspect_process(view="container")`,
`inspect_process(view="memory_trend")` — behind one `view` discriminator, and adds the
Phase 11 `inspect_process(view="runtime-config")` projection for GC / ThreadPool / tiered-comp startup settings,
plus the Phase 10.3 `inspect_process(view="resources")` FD / handle / socket inspector and
Phase 10.4 `inspect_process(view="requests-now")` for an in-flight ASP.NET Core request snapshot.
Each legacy view delegates to the same implementation as the removed tool of the same name, so
the payload under `data` is byte-identical to the legacy envelope's `data`.

**Parameters:**

| Name | Type | Default | Description |
|---|---|---|---|
| `view` | `"list" \| "info" \| "capabilities" \| "container" \| "memory_trend" \| "runtime-config" \| "resources" \| "requests-now"` | `"list"` | Which bootstrap projection to compute. |
| `processId` | `int?` | auto | Target PID. **Ignored when `view="list"`** (the list view is process-agnostic). When omitted on `view="memory_trend"` or `view="resources"` the server auto-resolves the lone reachable .NET process; `view="runtime-config"` and `view="requests-now"` also auto-resolve but still require a real .NET process because they open a live diagnostics path. |
| `durationSeconds` | `int` | `10` / `0` | Used by `view="memory_trend"` and `view="resources"`. Memory trend requires `>= 2`; resources uses `0` for a single snapshot (default) or `>= 2` for trend mode. |
| `sampleEverySeconds` | `int` | `2` | Used only by `view="memory_trend"` / `view="resources"`. Must be ≥ 1. |
| `depth` | `SamplingDepth?` | `Summary` | Used only by `view="container"`; forwarded to `inspect_process(view="container")`. |

**Returns:** `InspectProcessReport` — a standard envelope (`summary` / `hints` /
`error` / `resolvedProcess`) wrapping a `data` object that contains exactly one
populated field matching the requested view:

| `view` | `data` shape |
|---|---|
| `list` | `DotnetProcess[]` (see [`inspect_process(view="list")`](#inspect_process(view="list"))) |
| `info` | `DotnetProcess` (see [`inspect_process(view="info")`](#inspect_process(view="info"))) |
| `capabilities` | `DiagnosticCapabilities` (see [`inspect_process(view="capabilities")`](#inspect_process(view="capabilities"))) |
| `container` | `ContainerSignals` (see [`inspect_process(view="container")`](#inspect_process(view="container"))) |
| `memory_trend` | `MemoryTrend` (see [`inspect_process(view="memory_trend")`](#inspect_process(view="memory_trend"))) |
| `runtime-config` | `RuntimeConfigView` (see [`inspect_process(view="runtime-config")`](#inspect_process(view="runtime-config"))) |
| `resources` | `ProcessResources` (see [`inspect_process(view="resources")`](#inspect_process(view="resources"))) |
| `requests-now` | `InFlightHttpRequest[]` (see [`inspect_process(view="requests-now")`](#inspect_process(view="requests-now"))) |

**Recommended bootstrap sequence:**

```text
inspect_process(view="list")          # discover candidate PIDs (or rely on auto-resolve)
inspect_process(view="capabilities")  # confirm CoreCLR vs NativeAOT + ptrace/PSI/perf gates
inspect_process(view="container")       # cheap cgroup/PSI signals before any EventPipe session
inspect_process(view="memory_trend")    # lightweight leak signal — any OS process, no IPC
inspect_process(view="runtime-config")  # GC / ThreadPool / tiered-comp startup settings + filtered env vars
inspect_process(view="resources")       # unmanaged FD / socket / handle signal when heap is flat
inspect_process(view="requests-now")    # in-flight ASP.NET Core requests + current thread stacks
```

Unknown view values surface as the standard discriminator-dispatch error
(`error.kind = "InvalidArgument"`, `error.detail = "view"`).

---

## `inspect_process(view="list")`

> **Deprecated** — use [`inspect_process(view="list")`](#inspect_process). The
> payload is unchanged; the legacy tool remains registered and emits a
> `Deprecated` flag on `tools/list`.

Lists every .NET process on the local machine that exposes a Diagnostic IPC
endpoint (Unix socket on Linux, named pipe on Windows).

**Parameters:** none.

**Returns:** array of `DotnetProcess`:

```json
[
  {
    "processId": 12345,
    "commandLine": "/usr/bin/dotnet /app/MyApi.dll",
    "operatingSystem": "linux",
    "processArchitecture": "x64",
    "runtimeVersion": "10.0.0",
    "managedEntrypointAssemblyName": "MyApi"
  }
]
```

**Notes:** processes that respond too slowly or whose IPC endpoint is
unreachable are silently omitted.

---

## `inspect_process(view="info")`

> **Deprecated** — use [`inspect_process(view="info")`](#inspect_process).

Returns metadata for a single PID.

**Parameters:**

| Name | Type | Default | Description |
|---|---|---|---|
| `processId` | `int` | — | Target process id |

**Returns:** a single `DotnetProcess` (same shape as above) or `null` if the
process is gone / unreachable.

---

## `inspect_process(view="capabilities")`

> **Deprecated** — use [`inspect_process(view="capabilities")`](#inspect_process).

Probes the target by opening a short EventPipe session against the
`Microsoft-DotNETCore-SampleProfiler` provider. The presence/absence of sample
events is used to classify the runtime as **CoreCLR** vs **NativeAOT**.

**Parameters:**

| Name | Type | Default | Description |
|---|---|---|---|
| `processId` | `int` | — | Target process id |

**Returns:** `DiagnosticCapabilities`:

```json
{
  "processId": 12345,
  "runtime": "CoreClr",
  "runtimeVersion": "10.0.0",
  "canReadEventCounters": true,
  "canSampleCpu": true,
  "canCollectGcDump": true,
  "canCollectExceptions": true,
  "canCollectHttpActivity": true,
  "canCollectCustomEventSource": true,
  "canCollectProcessDump": true,
  "notes": "CoreCLR runtime detected via SampleProfiler events."
}
```

**Notes:** always call this **first** in a session. The result tells the LLM
(or human) which other tools can be used on the target. NativeAOT will return
`runtime = "NativeAot"` and `canSampleCpu = false`.

---

## `inspect_process(view="memory_trend")`

> **Deprecated** — use [`inspect_process(view="memory_trend")`](#inspect_process).

Samples OS-level memory metrics at regular intervals over a configurable window
and computes per-second deltas and a growth verdict. Works on **any** runtime
(CoreCLR, NativeAOT, even non-.NET processes) — no EventPipe session required.

Use this as a **lightweight memory-leak signal** before reaching for heap dumps.
It answers "is the process growing and how fast?" without walking the heap.

**Sources:**
- **Linux**: `/proc/<pid>/smaps_rollup` (Rss, Pss, Anonymous) and
  `/proc/<pid>/stat` fields 10 & 12 (minflt / majflt). Pure file reads —
  no privileges, no EventPipe.
- **Windows**: `GetProcessMemoryInfo(PROCESS_MEMORY_COUNTERS_EX)`:
  `WorkingSetSize` (RSS), `PrivateUsage` (private committed bytes),
  `PageFaultCount`. Requires `PROCESS_QUERY_INFORMATION` access to the target.

**Parameters:**

| Name | Type | Default | Description |
|---|---|---|---|
| `processId` | `int?` | auto | Target process id |
| `durationSeconds` | `int` | `10` | Observation window length in seconds. Must be ≥ 2. |
| `sampleEverySeconds` | `int` | `2` | Interval between consecutive samples in seconds. Must be ≥ 1. |

**Returns:** `MemoryTrend`:

```json
{
  "processId": 12345,
  "windowStart": "2026-05-18T20:00:00Z",
  "windowEnd": "2026-05-18T20:00:10Z",
  "samples": [
    {
      "timestamp": "2026-05-18T20:00:00Z",
      "rssBytes": 104857600,
      "pssBytes": 52428800,
      "privateAnonBytes": 83886080,
      "heapRegionBytes": null,
      "majorFaults": 12,
      "minorFaults": 50000
    }
  ],
  "deltas": {
    "rssBytesPerSec": 1200000.0,
    "pssBytesPerSec": 600000.0,
    "majorFaultsPerSec": 0.2
  },
  "verdict": "growing",
  "notes": []
}
```

**Verdict heuristic:** RSS growth > 1 MiB/s → `growing`; RSS decrease > 1
MiB/s → `shrinking`; otherwise → `stable`. All three values are
stable-but-informative labels — they do not distinguish between heap and
stack allocations.

**Field notes:**
- `pssBytes` is Linux-only (Proportional Set Size — shared pages charged
  proportionally). Always `null` on Windows.
- `heapRegionBytes` is `null` on both platforms (requires a full
  `/proc/<pid>/smaps` walk; omitted for cost reasons).
- On Windows, `majorFaults` is always `0` — Windows does not separate
  major/minor faults; the combined count appears in `minorFaults`.

**Next-action hints:**
- `verdict = "growing"` → suggests `inspect_heap(source="live")` (identify dominant
  retainers) and `inspect_process(view="container")` (cross-check against cgroup limits).
- `verdict = "stable"` or `"shrinking"` → suggests `collect_events(kind="counters")`.

---

## `inspect_process(view="runtime-config")`

Cheap startup-configuration snapshot for questions like "is this Server GC?", "what are the ThreadPool min/max settings?", and "did someone override tiered compilation?".

- **GC / ThreadPool**: best-effort ClrMD live attach. On Linux, ptrace restrictions degrade to `notes[]` instead of failing the whole view.
- **Tiered compilation**: sourced from startup env overrides (`DOTNET_TieredCompilation`, `DOTNET_TC_QuickJit`, `DOTNET_TieredPGO`, plus `COMPlus_` aliases when present).
- **Environment variables**: Linux reads `/proc/<pid>/environ`; Windows currently returns an explanatory note and an empty `envVars[]`.
- **Security boundary**: `envVars[]` is strictly filtered to `DOTNET_`, `COMPlus_`, `ASPNETCORE_`, and `DOTNET_SYSTEM_` prefixes. Everything else is intentionally dropped.
- **AppContext switches**: forward-compatible field shape; today returns `[]` with a note because the runtime does not expose them out-of-process.

**Parameters:**

| Name | Type | Default | Description |
|---|---|---|---|
| `processId` | `int?` | auto | Target .NET process id. When omitted the server auto-resolves the lone reachable .NET process. |

**Returns:** `RuntimeConfigView`:

```json
{
  "processId": 12345,
  "gc": {
    "isServerGc": false,
    "isConcurrent": true,
    "isBackground": true,
    "heapCount": 1,
    "largeObjectHeapCompactionMode": null
  },
  "threadPool": {
    "minWorkerThreads": 1,
    "maxWorkerThreads": 32767,
    "minIocpThreads": 1,
    "maxIocpThreads": 1000,
    "hillClimbingEnabled": true
  },
  "tieredCompilation": {
    "enabled": true,
    "quickJitEnabled": true,
    "dynamicPgoEnabled": true
  },
  "envVars": [
    { "name": "DOTNET_TieredCompilation", "value": "1" },
    { "name": "ASPNETCORE_URLS", "value": "http://127.0.0.1:0" }
  ],
  "appContextSwitches": [],
  "notes": [
    "Environment variables are filtered to known runtime prefixes (DOTNET_ / COMPlus_ / ASPNETCORE_ / DOTNET_SYSTEM_); all other process env vars are intentionally omitted as a security boundary.",
    "AppContext switches are not introspectable without an in-process probe; appContextSwitches is currently empty by design."
  ]
}
```

---

## `inspect_process(view="resources")`

Cheap OS-level resource inspector for the classic "RSS grows but `gc-heap-size` stays flat" case.

- **Linux**: counts `/proc/<pid>/fd`, classifies symlink targets (`socket:[...]`, `/...`, `pipe:[...]`, `anon_inode:[eventfd]`), aggregates TCP states from `/proc/<pid>/net/tcp{,6}`, and parses `Max open files` from `/proc/<pid>/limits`.
- **Windows**: calls `GetProcessHandleCount`; FD/socket breakdowns stay `null` with a note.

**Parameters:**

| Name | Type | Default | Description |
|---|---|---|---|
| `processId` | `int?` | auto | Target process id. Explicit values bypass .NET IPC resolution, so any OS pid is accepted. |
| `durationSeconds` | `int` | `0` | `0` = single snapshot; values `>= 2` enable trend mode. |
| `sampleEverySeconds` | `int` | `2` | Interval between trend samples. Must be ≥ 1. Ignored when `durationSeconds = 0`. |

**Returns:** `ProcessResources`:

```json
{
  "processId": 12345,
  "capturedAt": "2026-05-25T22:40:00Z",
  "fdCount": 186,
  "handleCount": null,
  "fd": { "sockets": 42, "regular": 96, "pipes": 16, "eventfds": 2, "other": 30 },
  "sockets": { "established": 12, "timeWait": 51, "closeWait": 0, "listen": 2, "other": 1 },
  "limits": { "noFileSoft": 1024, "noFileHard": 1024, "noFileUsageFraction": 0.1816 },
  "notes": [],
  "trend": null
}
```

`trend.samples[]` repeats the same headline fields (`fdCount`, `handleCount`, `fd`, `sockets`, `limits`) per sample, with the top-level properties set to the latest sample.

**Next-action hints:**
- `closeWait > 100` and rising → `collect_events(kind="event_source", providerName="System.Net.Http")` to confirm undisposed responses / client misuse.
- `noFileUsageFraction > 0.85` → consider `collect_process_dump` before the process hits `EMFILE` / "Too many open files".
- huge `timeWait` with flat `fdCount` → connection churn / pooling issue, again best cross-checked with `System.Net.Http` events.

---

## `inspect_process(view="requests-now")`

Short ASP.NET Core request snapshot for the "which requests are hanging right now?" question.

- Opens a ~2 s EventPipe window against the ASP.NET Core `HttpRequestIn` activity stream.
- Keeps only requests whose start event was observed **without** a matching stop before the window closed.
- Captures one live thread snapshot and maps the observed OS thread id back to top managed frames.
- Requires the `ptrace` scope because the enrichment step uses the same live-attach path as `collect_thread_snapshot`.

**Parameters:**

| Name | Type | Default | Description |
|---|---|---|---|
| `processId` | `int?` | auto | Target .NET process id. When omitted the server auto-resolves the lone reachable .NET process. |

**Returns:** `InFlightHttpRequest[]`:

```json
[
  {
    "traceId": "4b89c4e2f7c4b0d7b34d2d9739f52f01",
    "endpoint": "/slow-hang",
    "method": "GET",
    "startedAtMs": 1840.0,
    "threadId": 12345,
    "topFrames": [
      "System.Threading.Tasks.Task.Delay(Int32, CancellationToken)",
      "BadCodeSample.Program+<>c.<<Main>$>b__0_11>d.MoveNext()"
    ]
  }
]
```

`method` and `endpoint` are best-effort projections from the request activity metadata. If ASP.NET Core did not stamp those fields before the snapshot, the server returns `"(unknown)"` rather than dropping the request row.

---

## `collect_events`

**Canonical EventPipe collector** (RFC 0002 §4.5). A single tool that dispatches
by `kind` to the underlying counters / exceptions / gc / event_source /
activities / logs / threadpool collector. New clients should call `collect_events` instead of the
legacy entrypoints; the legacy tools remain registered and behaviorally
identical, but each carries a `DEPRECATED` notice and will be removed in
`0.7.0`.

**Parameters:**

| Name | Type | Default | Description |
|---|---|---|---|
| `kind` | `string` | — | One of `counters`, `exceptions`, `gc`, `event_source`, `activities`, `logs`, `jit`. Case-sensitive. |
| `kind` | `string` | — | One of `counters`, `exceptions`, `gc`, `event_source`, `activities`, `logs`, `threadpool`. Case-sensitive. |
| `kind` | `string` | — | One of `counters`, `exceptions`, `gc`, `event_source`, `activities`, `logs`, `db`. Case-sensitive. |
| `processId` | `int?` | auto | Target process id. |
| `durationSeconds` | `int` | 5 (counters) / 10 (others) | Collection window. |
| `providers` / `meters` / `intervalSeconds` / `maxInstrumentTimeSeries` | counters only | — | Same as [`collect_events(kind="counters")`](#collect_events(kind="counters")). |
| `maxRecent` | exceptions only | 100 | Same as [`collect_events(kind="exceptions")`](#collect_events(kind="exceptions")). |
| `maxEvents` | gc / event_source / logs only | 200 (`gc`, `event_source`) / 500 (`logs`) | Same as the underlying tool. |
| `providerName` / `keywords` / `eventLevel` / `depth` / `unsafeProvider` | event_source only | — | Same as [`collect_events(kind="event_source")`](#collect_events(kind="event_source")). |
| `sources` / `maxActivities` | activities only | — | Same as [`collect_events(kind="activities")`](#collect_events(kind="activities")). |
| `categories` / `minLevel` / `maxMessageBytes` / `depth` | logs only | — | Same as [`collect_events(kind="logs")`](#collect_events(kind="logs")). |
| `depth` | jit only | `Summary` | Same as [`collect_events(kind="jit")`](#collect_events(kind="jit")). |

**Returns:** `CollectEventsEnvelope` — a polymorphic record that carries the
`kind` discriminator plus exactly one populated payload field
(`counters` / `exceptions` / `gc` / `eventSource` / `activities` / `logs` / `jit`). The
| `depth` | threadpool only | `Summary` | `Summary` keeps the headline counts + top origins inline; `Detail` / `Raw` keep full timelines + hill-climbing samples inline. |

**Returns:** `CollectEventsEnvelope` — a polymorphic record that carries the
`kind` discriminator plus exactly one populated payload field
(`counters` / `exceptions` / `gc` / `eventSource` / `activities` / `logs` / `threadPool`). The
| `intervalSeconds` / `depth` | db only | `1` / `Summary` | SqlClient EventCounter refresh interval + inline verbosity for the curated DB view. |

**Returns:** `CollectEventsEnvelope` — a polymorphic record that carries the
`kind` discriminator plus exactly one populated payload field
(`counters` / `exceptions` / `gc` / `eventSource` / `activities` / `logs` / `db`). The
envelope's `summary`, `hints`, `handle`, `handleExpiresAt`, and
`resolvedProcess` are passed through from the underlying collector verbatim,
so `query_snapshot` drilldowns continue to work unchanged.

**Authorization.** The dispatcher is gated by `RequireAnyScope("read-counters","eventpipe")`
and re-checks the per-kind scope inside the call so the boundaries of RFC 0001
§2 are preserved: `kind="counters"` requires `read-counters`, every other
kind requires `eventpipe` (`event_source` additionally honors the existing
`eventsource-any` modifier).

---

## `collect_sample`

**Canonical bounded-time sampler** (RFC 0002 §4.2). A single tool that
dispatches by `kind` to the underlying CPU / off-CPU / allocation sampler.
New clients should call `collect_sample` instead of the three legacy entry
points; the legacy tools remain registered and behaviorally identical, but
each carries a `DEPRECATED` notice and will be removed in `0.9.0`.

**Parameters:**

| Name | Type | Default | Description |
|---|---|---|---|
| `kind` | `string` | `cpu` | One of `cpu`, `off_cpu`, `allocation`. Case-sensitive. |
| `processId` | `int?` | auto | Target process id. |
| `durationSeconds` | `int` | `10` | Sampling window. ≥ 1. |
| `topN` | `int` | `25` | Top hotspots / blocking stacks / types. |
| `depth` | `SamplingDepth` | `Summary` | Verbosity; applies to `cpu` / `off_cpu`. Ignored by `allocation`. |
| `symbolPath` | `string?` | `null` | `cpu` / `off_cpu` only. Symbol search path; remote `srv*http(s)://…` segments are denied unless allowlisted (issue #165 / M3). |
| `resolveSourceLines` | `bool` | `true` | `cpu` only. Same as [`collect_sample(kind="cpu")`](#collect_sample(kind="cpu")). |
| `maxResolvedSources` | `int?` | `topN` | `cpu` only. |
| `resolveMethodInstantiations` / `maxResolvedMethodInstantiations` | — | — | `cpu` only. Same as `collect_sample(kind="cpu")`. |

**Returns:** `CollectSampleEnvelope` — a polymorphic record carrying the
`kind` discriminator plus exactly one populated payload field
(`cpu` / `offCpu` / `allocation`). The envelope's `summary`, `hints`,
`handle`, `handleExpiresAt`, and `resolvedProcess` are passed through from
the underlying sampler verbatim, so `query_snapshot(view="call-tree")` and
`query_snapshot` drilldowns continue to work unchanged.

**Platform notes.** `kind="off_cpu"` requires Linux (`perf record -e sched_switch`)
or Windows admin (NT Kernel Logger ContextSwitch); on unsupported hosts the
unified tool returns the same `NotSupported` / `PermissionDenied` envelope the
legacy `collect_sample(kind="off_cpu")` returns. `kind="allocation"` works on CoreCLR
and NativeAOT, but on NativeAOT GCAllocationTick events carry an empty
TypeName — surfaced via the envelope summary so the LLM knows to fall back
to `kind="cpu"` for per-site attribution.

**Authorization.** Gated by `RequireScope("eventpipe")`, matching the three
legacy samplers verbatim.

---

## `collect_events(kind="counters")`

> **Deprecated — call [`collect_events`](#collect_events) with `kind="counters"`.**
> Behaviorally identical; will be removed in `0.7.0`.


Subscribes to one or more legacy EventCounter providers and, optionally, one or
more Meter names through `System.Diagnostics.Metrics`. Returns the latest
EventCounter value per counter plus the latest Meter time series / histogram
snapshot seen over a fixed window.

**Parameters:**

| Name | Type | Default | Description |
|---|---|---|---|
| `processId` | `int` | — | Target process id |
| `durationSeconds` | `int` | `5` | Collection window. Must be ≥ 1. |
| `providers` | `string[]?` | see below | Legacy EventCounter provider names. `null` uses defaults; `[]` disables legacy EventCounters. |
| `meters` | `string[]?` | `null` | Meter names forwarded to `System.Diagnostics.Metrics`. Null/empty disables Meter collection. |
| `intervalSeconds` | `int` | `1` | Refresh interval for both EventCounters and Meter aggregation. |
| `maxInstrumentTimeSeries` | `int` | `1000` | Max Meter time series / histograms retained before the collector caps results and emits a `Notes[]` warning. |

When `providers` is null the defaults are:
`System.Runtime`, `Microsoft.AspNetCore.Hosting`, `Microsoft-AspNetCore-Server-Kestrel`.

**Returns:** `CounterSnapshot`:

```json
{
  "processId": 12345,
  "startedAt": "2026-05-18T20:00:00Z",
  "duration": "00:00:05",
  "counters": [
    {
      "provider": "System.Runtime",
      "name": "cpu-usage",
      "displayName": "CPU Usage",
      "value": 23.4,
      "unit": "%",
      "kind": "Mean"
    }
  ],
  "meters": [
    {
      "meter": "Microsoft.AspNetCore.Hosting",
      "instrument": "http.server.request.duration",
      "unit": "s",
      "kind": "Histogram<double>",
      "tags": {
        "method": "GET"
      },
      "lastValue": null,
      "rate": null,
      "histogram": {
        "count": 42,
        "sum": 1.84,
        "p50": 0.031,
        "p95": 0.084,
        "p99": 0.120
      }
    }
  ],
  "notes": [
    "TimeSeriesLimitReached: capped at 1000 series."
  ]
}
```

When Meter data is present, `SamplingDepth.Summary` keeps the headline EventCounters
and also includes `http.server.request.duration` p95 when available.

---

## `collect_sample(kind="cpu")`

> **DEPRECATED (0.9.0).** Call [`collect_sample(kind="cpu", …)`](#collect_sample) instead. The legacy tool remains registered and behaviorally identical during the deprecation window (RFC 0002 §4.2 / issue #210).

Captures a CPU sample via the `Microsoft-DotNETCore-SampleProfiler` provider,
writes a temporary `.nettrace`, parses it with `TraceLog` and aggregates the
top-N hotspots by inclusive and exclusive sample counts.

**Parameters:**

| Name | Type | Default | Description |
|---|---|---|---|
| `processId` | `int` | — | Target process id |
| `durationSeconds` | `int` | `10` | Sampling window. ≥ 1. |
| `topN` | `int` | `25` | Maximum hotspots returned. ≥ 1. |
| `resolveSourceLines` | `bool` | `true` | Resolve top hotspots to source file:line via PDB / SourceLink. |
| `symbolPath` | `string?` | `null` | Optional symbol search path used when `resolveSourceLines=true`. **Remote symbol servers are denied by default** (issue #165 / M3): any `srv*http(s)://…` segment must point at a host listed under `Diagnostics:SymbolServerAllowlist`, otherwise the call fails with a `SymbolServerNotAllowed` envelope. Local paths always pass through. See [Security gates](#security-gates-b4). |
| `maxResolvedSources` | `int?` | `topN` | Cap on how many hotspots get source resolution. |
| `resolveMethodInstantiations` | `bool` | `false` | Opt-in ClrMD attach after sampling to recover closed generic method signatures for the hottest managed frames. CoreCLR only; on Linux requires `CAP_SYS_PTRACE` (or `ptrace_scope=0`) and briefly suspends the target. |
| `maxResolvedMethodInstantiations` | `int?` | `topN` | Cap on how many hotspots get ClrMD generic-instantiation enrichment. |
| `depth` | `SamplingDepth` | `Summary` | `Summary` returns the top 3 hotspots inline; `Detail` / `Raw` return the requested `topN`. |

**Returns:** `CpuSample`:

```json
{
  "processId": 12345,
  "startedAt": "2026-05-18T20:00:00Z",
  "duration": "00:00:10",
  "totalSamples": 4218,
  "topHotspots": [
    {
      "frame": { "module": "MyApi", "method": "MyApi.Service.DoWork(int)" },
      "inclusiveSamples": 1820,
      "exclusiveSamples": 320
    }
  ],
  "symbolSource": "ElfDemangled"
}
```

`symbolSource` is populated for **NativeAOT** samples only (see #35) and
reports the aggregate symbol-resolution quality of `topHotspots`:

- `ElfDemangled` — every managed frame went through the demangler. Trust the
  names as-is.
- `ElfMangled` — perf returned managed-looking symbols but demangling did not
  apply (e.g. lookup table missing). Names are still usable but may be `S_P_…`-style.
- `Native` — frames are non-managed (libc / P/Invoke / kernel). Expected for
  threadpool/GC threads.
- `Stripped` — perf returned `[unknown]` or raw addresses; names are not
  actionable. Likely missing build-id / PDB on the host.
- `Mixed` — quality varies across `topHotspots`. Inspect per-frame.
- `Unknown` / omitted — CoreCLR sample (the EventPipe path resolves managed
  names directly; this field does not apply).

**Routing.** `collect_sample(kind="cpu")` dispatches based on
`inspect_process(view="capabilities")`:

- **CoreCLR (Linux + Windows)** — EventPipe `SampleProfiler` over the
  diagnostic socket; managed frames carry the `(mvid, token)` handoff.
- **NativeAOT / Linux** — system-wide `perf record` (frames are native;
  managed names recovered from the AOT `.symbols.map` sidecar when present).
- **NativeAOT / Windows** — NT Kernel Logger `PerfInfo/SampledProfile` via
  ETW; admin elevation (or `SeSystemProfilePrivilege`) required. Frames are
  native; managed names recovered from the PE export table + PDB.

Confirm the dispatch path up front with `inspect_process(view="capabilities")` →
`data.canSampleCpu`. Coverage and AOT caveats are summarized in
[`aot-coverage.md`](./aot-coverage.md).

**NativeAOT/Linux perf install.** On Debian/Ubuntu/WSL the distro ships a
wrapper at `/usr/bin/perf` that fails unless the matching
`linux-tools-$(uname -r)` package is installed. The sampler auto-discovers a
working binary by probing `/usr/lib/linux-tools-*/perf` (kernel-matched first,
then newest-first); when nothing usable is found, `IsAvailable` returns false
and the tool reports `not_supported`. Install with:

```bash
sudo apt install linux-tools-$(uname -r) linux-tools-generic
```

**Sampling rate** is the runtime default (~1 kHz). A 10-second window typically
yields a few thousand samples; bump `durationSeconds` for sparse workloads.

**Long-running pattern:** this tool supports MCP Tasks (`execution.taskSupport:
"optional"`). Spec clients should use task-augmented `tools/call` + `tasks/get` /
`tasks/result`; for clients that don't implement Tasks, use the in-request
`notifications/progress` + `notifications/cancelled` flow described under
[MCP-native progress and cancellation](#mcp-native-progress-and-cancellation-rfc-0002-73-7--issue-211).

## Symbol resolution

Tools that resolve external symbols now share the same precedence chain:

1. explicit tool parameter `symbolPath`
2. server startup env `MCP_SYMBOL_PATH`
3. host env `_NT_SYMBOL_PATH`
4. local fallback paths (typically the target `MainModule` directory; `collect_sample(kind="cpu")`
   also appends module directories discovered in the trace)

`symbolPath` values use TraceEvent / `SymbolReader`'s NT-style syntax on every OS.
Common examples:

- `srv*C:\\symbols*https://msdl.microsoft.com/download/symbols`
- `cache*/tmp/sym;srv*https://nuget.smbsrc.net`
- local PDB-only default: omit `symbolPath` and keep the PDB next to the target binary

The same override shape is exposed by `collect_sample(kind="cpu")`, `collect_sample(kind="off_cpu")`,
`collect_thread_snapshot`, `inspect_heap(source="dump")`, and `inspect_heap(source="live")`.

**Opt-in closed generics (`resolveMethodInstantiations`).** On Linux, EventPipe alone only knows the
open `MethodDef` for generic methods like `Echo<T>`. When you enable this flag, the server performs
an additional ClrMD attach after the trace ends, resolves the hottest instruction pointers back to
closed runtime methods, and stamps `MethodIdentity.ClosedSignature` plus
`MethodIdentity.GenericTypeArguments.Method`. This keeps the default EventPipe path lightweight while
making LINQ / MediatR / serializer hotspots far more operator-friendly when you explicitly need the
closed form.

---

## `collect_sample(kind="allocation")`

> **DEPRECATED (0.9.0).** Call [`collect_sample(kind="allocation", …)`](#collect_sample) instead. The legacy tool remains registered and behaviorally identical during the deprecation window (RFC 0002 §4.2 / issue #210).

Captures allocation samples from the target process via `GCAllocationTick`
events from `Microsoft-Windows-DotNETRuntime` (keyword `GCKeyword=0x1`, level
Verbose). The GC fires this event roughly every **100 KB of total managed
allocations** and carries the TypeName of the most recently allocated object
plus a call stack. The call stack is accessible via `query_snapshot(view="call-tree")` using the
handle returned by this tool.

**CoreCLR**: TypeName is fully populated with managed type names. The call tree
resolves to managed method names via rundown events. `MethodIdentity` (MVID +
metadata token) is emitted for top-N frames, enabling the assembly-mcp handoff.

**NativeAOT**: `GCAllocationTick` events fire, but the runtime **does not**
populate the `TypeName` field — managed type metadata is stripped at compile
time. All events roll up under `<unknown>`. The call tree is captured but
contains native frame addresses only. See [`aot-coverage.md`](./aot-coverage.md)
for the full NativeAOT diagnostic matrix.

**Parameters:**

| Name | Type | Default | Description |
|---|---|---|---|
| `processId` | `int?` | auto | Target process id (optional — auto-selects when only one .NET process is visible) |
| `durationSeconds` | `int` | `10` | Sampling window. Must be ≥ 1. |
| `topN` | `int` | `25` | Maximum types per ranked list. Must be ≥ 1. |

**Returns:** `AllocationSample` with a drilldown `handle`:

```json
{
  "processId": 12345,
  "startedAt": "2026-05-18T20:00:00Z",
  "duration": "00:00:10",
  "totalEvents": 14250,
  "totalBytes": 1469161472,
  "topByBytes": [
    { "typeName": "System.String", "totalBytes": 1400000000, "eventCount": 14000, "dominantKind": "Small" },
    { "typeName": "System.Byte[]", "totalBytes": 60000000, "eventCount": 200, "dominantKind": "Large" }
  ],
  "topByCount": [
    { "typeName": "System.String", "totalBytes": 1400000000, "eventCount": 14000, "dominantKind": "Small" }
  ]
}
```

`TopByBytes` ranks by total allocated bytes — the dominant signal for allocation
pressure. `TopByCount` ranks by sampling event count — useful when many small
types compete with one large-object type.

**Notes on sampling semantics:** `GCAllocationTick` is a sampled event, not
an instrumented one. It samples the *most recently allocated* type when the
total allocation counter crosses each 100 KB threshold. High-frequency types
are sampled proportionally more often, making the top-N ranking statistically
accurate for steady workloads.

**Run after** `collect_events(kind="counters")` shows elevated `gen-0-gc-count`,
`gen-1-gc-count`, or growing `gc-heap-size`. Use `query_snapshot(view="call-tree")` with the
returned handle to find which allocation sites are responsible.

---

## `collect_events(kind="exceptions")`

> **Deprecated — call [`collect_events`](#collect_events) with `kind="exceptions"`.**
> Behaviorally identical; will be removed in `0.7.0`.


exception thrown by the process during the window.

**Parameters:**

| Name | Type | Default | Description |
|---|---|---|---|
| `processId` | `int` | — | Target process id |
| `durationSeconds` | `int` | `10` | Window length |
| `maxRecent` | `int` | `100` | Maximum exception details to return |

**Returns:** `ExceptionSnapshot`:

```json
{
  "processId": 12345,
  "startedAt": "2026-05-18T20:00:00Z",
  "duration": "00:00:10",
  "totalExceptions": 42,
  "byType": [
    { "exceptionType": "System.InvalidOperationException", "count": 30 },
    { "exceptionType": "System.TimeoutException", "count": 12 }
  ],
  "recent": [
    {
      "timestamp": "2026-05-18T20:00:01.123Z",
      "exceptionType": "System.InvalidOperationException",
      "exceptionMessage": "Sequence contains no elements",
      "exceptionHResult": "0x80131509",
      "threadId": 17
    }
  ],
  "recentCap": 100
}
```

**Notes:** also catches "first-chance" exceptions caught by the app — useful
for detecting error rates much higher than the response logs suggest.

`totalExceptions` and `byType` are always exact for the window. `recent` is
capped to `maxRecent` (default `100`, echoed back as `recentCap`); when
`totalExceptions > recentCap` it contains the first `recentCap` exceptions
observed, not a random sample. Raise `maxRecent` for storms where the tail
matters; lower it when you only want a quick signal.

**Long-running pattern:** this tool supports MCP Tasks (`execution.taskSupport:
"optional"`). Spec clients should use task-augmented `tools/call` + `tasks/get` /
`tasks/result`. Clients that don't implement Tasks should use the in-request
`notifications/progress` + `notifications/cancelled` flow.

---

## `collect_events(kind="gc")`

> **Deprecated — call [`collect_events`](#collect_events) with `kind="gc"`.**
> Behaviorally identical; will be removed in `0.7.0`.


Subscribes to the runtime `GC` keyword, pairs `GCStart`/`GCStop` events and
returns aggregate + per-collection details.

**Parameters:**

| Name | Type | Default | Description |
|---|---|---|---|
| `processId` | `int` | — | Target process id |
| `durationSeconds` | `int` | `10` | Window length |
| `maxEvents` | `int` | `200` | Cap on individual GC events returned |

**Long-running pattern:** this tool supports MCP Tasks (`execution.taskSupport:
"optional"`). Spec clients should use task-augmented `tools/call`; clients that
don't implement Tasks should use the in-request `notifications/progress` +
`notifications/cancelled` flow.

**Returns:** `GcSummary`:

```json
{
  "processId": 12345,
  "startedAt": "2026-05-18T20:00:00Z",
  "duration": "00:00:10",
  "totalCollections": 18,
  "totalPauseTime": "00:00:00.0420000",
  "maxPauseTime": "00:00:00.0150000",
  "generations": [
    { "generation": 0, "count": 14 },
    { "generation": 1, "count": 3 },
    { "generation": 2, "count": 1 }
  ],
  "events": [
    {
      "timestamp": "2026-05-18T20:00:01.500Z",
      "generation": 0,
      "reason": "AllocSmall",
      "type": "NonConcurrentGC",
      "pauseDuration": "00:00:00.0021000"
    }
  ]
}
```

**Notes:** to capture a full gcdump (heap snapshot), use `collect_process_dump`
with `dumpType = "WithHeap"` and analyze offline with `dotnet-dump`.

---

## `collect_events(kind="activities")`

> **Deprecated — call [`collect_events`](#collect_events) with `kind="activities"`.**
> Behaviorally identical; will be removed in `0.7.0`.


Captures `ActivitySource` spans through the `Microsoft-Diagnostics-DiagnosticSource`
EventPipe bridge, keeping completed span records inline and grouped rollups behind
`query_snapshot`.

**Parameters:**

| Name | Type | Default | Description |
|---|---|---|---|
| `processId` | `int` | — | Target process id |
| `sources` | `string[]?` | `null` | Optional `ActivitySource` filters (`*` / `?` wildcards supported) |
| `durationSeconds` | `int` | `10` | Window length |
| `maxActivities` | `int` | `200` | Cap on captured span records retained inline + in the handle artifact |

**Returns:** `ActivityCapture`:

```json
{
  "processId": 12345,
  "sourceFilters": ["MyCompany.Checkout*"],
  "startedAt": "2026-05-18T20:00:00Z",
  "duration": "00:00:10",
  "totalActivities": 12,
  "completedActivities": 12,
  "activities": [
    {
      "sourceName": "MyCompany.Checkout",
      "operationName": "POST /checkout",
      "id": "00-3b2dc9c6a0b7dc27ba8e290f198d98f4-9f10a33a49390375-01",
      "parentId": null,
      "traceId": "3b2dc9c6a0b7dc27ba8e290f198d98f4",
      "spanId": "9f10a33a49390375",
      "parentSpanId": null,
      "startedAt": "2026-05-18T20:00:00.120Z",
      "stoppedAt": "2026-05-18T20:00:00.188Z",
      "duration": "00:00:00.0680000",
      "tags": { "http.method": "POST", "db.system": "sqlserver" }
    }
  ],
  "bySource": [
    {
      "sourceName": "MyCompany.Checkout",
      "count": 12,
      "completedCount": 12,
      "averageDurationMs": 32.7,
      "maxDurationMs": 68.0
    }
  ],
  "byOperation": [
    {
      "sourceName": "MyCompany.Checkout",
      "operationName": "POST /checkout",
      "count": 12,
      "completedCount": 12,
      "averageDurationMs": 32.7,
      "maxDurationMs": 68.0
    }
  ]
}
```

**Drilldown:** `query_snapshot(handle, view="bySource" | "byOperation" | "activities")`
re-projects the same capture window without reopening EventPipe.

**Notes:**

- The collector listens to `Activity/Stop` bridge events, so every returned row is a
  completed span with duration + tags already populated.
- `sources` matches `ActivitySource.Name`, not operation names.
- The provider supports a single Activity listener per session; this tool claims it for
  the duration of the capture window.

---

## `collect_events(kind="logs")`

Collects a curated `ILogger` view from the `Microsoft-Extensions-Logging`
EventSource, keeping per-level counts, per-category rollups, a bounded recent
ring buffer, and redacted scope / exception detail when `depth != "Summary"`.

**Parameters:**

| Name | Type | Default | Description |
|---|---|---|---|
| `processId` | `int?` | — | Target process id |
| `durationSeconds` | `int` | `10` | Window length |
| `categories` | `string[]?` | `null` | Optional case-insensitive glob filters for logger categories |
| `minLevel` | `string` | `Information` | Minimum retained level: `Trace`, `Debug`, `Information`, `Warning`, `Error`, `Critical` |
| `maxEvents` | `int` | `500` | Cap on retained recent log entries |
| `maxMessageBytes` | `int` | `4096` | Per-message / scope / exception UTF-8 truncation cap |
| `depth` | `SamplingDepth` | `Summary` | `Summary` drops `recent`; `Detail` / `Raw` also enable `MessageJson` for exception + scope detail |

**Returns:** `LogSnapshot` with:

- `totalEvents`
- `eventsByLevelTrace|Debug|Information|Warning|Error|Critical`
- `byCategory` (`LogCategoryGroup[]` sorted by count)
- `recent` (`LogEntry[]`, bounded by `maxEvents`)
- `truncated` + `notes`

`LogEntry` carries `timestamp`, `level`, `category`, `eventId`, `eventName`,
`message`, optional `exceptionType` / `exceptionMessage`, and optional redacted
`scopes`.

**Drilldown:** `query_snapshot(handle, view="summary" | "byCategory" | "byLevel" | "recent" | "errors")`.

**Notes:**

- `MessageJson` is enabled only when `depth != "Summary"` to reduce collector overhead.
- Messages and scope values always pass through `SensitiveDataRedactor` before they are retained.
- When `truncated=true`, the collector dropped oldest retained entries after `maxEvents`.

---

## `collect_events(kind="jit")`

Collects CLR JIT / tiered-compilation activity from `Microsoft-Windows-DotNETRuntime`,
reconstructing inclusive JIT time from `MethodJittingStarted` → `MethodLoadVerbose`
pairs and tracking Tier0 vs Tier1, ReadyToRun hits/miss-then-jit, ReJIT, OSR,
and IL-map counts.
## `collect_events(kind="threadpool")`

Collects a curated ThreadPool starvation view from the runtime `ThreadingKeyword`
(`Microsoft-Windows-DotNETRuntime`, `0x10000`): per-second worker + IOCP timelines,
hill-climbing transitions/reasons, best-effort effective min/max settings when the
runtime emits `ThreadPoolMinMaxThreadsChanged`, and top work-item origins when EventPipe
exposes enqueue call stacks.
## `collect_events(kind="db")`

Collects a curated database view by combining EF Core command activities with
SqlClient command/pool telemetry. The collector groups commands by
`(CommandTextHash, ConnectionStringSanitized)`, computes `count`, `totalMs`,
`maxMs`, `p95Ms`, flags N+1 patterns when the same command repeats more than 10
 times under the same parent activity / trace, and snapshots SqlClient pool
 counters when available.

**Parameters:**

| Name | Type | Default | Description |
|---|---|---|---|
| `processId` | `int?` | — | Target process id |
| `durationSeconds` | `int` | `10` | Window length |
| `depth` | `SamplingDepth` | `Summary` | `Summary` keeps only the hottest 10 methods inline; `Detail` / `Raw` return every observed method row |

**Returns:** `JitSnapshot` with:

- `jitStartCount`, `completedCompilations`, `uniqueMethods`
- `distribution` (`tier0`, `tier1`, `readyToRun`, `r2rHit`, `r2rMissThenJit`)
- `reJitCount`, `osrCount`, `ilMapCount`, `r2rLookupCount`
- `tier1Percent`, `r2rHitRatePercent`, `healthCheck`
- `methods` (`JitMethodSummary[]` sorted by `inclusiveJitTimeMs` descending)
- `notes`

`JitMethodSummary` carries `methodNamespace`, `methodName`, `methodSignature`,
`displayName`, `inclusiveJitTimeMs`, `compilationCount`, `lastOptimizationTier`,
per-tier counts, `reJitCount`, `osrCount`, and `hasIlMap`.

**Drilldown:** `query_snapshot(handle, view="summary" | "topMethods" | "tierDistribution" | "reJIT")`.

**Notes:**

- The collector enables the runtime's JIT + JIT tracing keywords **plus** IL-map / compilation-diagnostic keywords so ReadyToRun lookup and IL-map events are visible in the same window.
- `R2R hit rate` is computed over all observed `r2rLookupCount` lookups; `R2RMissThenJit` remains a separate correlation metric for misses that fell back to JIT within the same window.
- `OSR` is surfaced from `OptimizationTier=OptimizedTier1OSR` on `MethodLoadVerbose`.
| `depth` | `SamplingDepth` | `Summary` | `Summary` keeps headline counts + top origins inline; `Detail` / `Raw` keep full timelines + hill-climbing samples inline |

**Returns:** `ThreadPoolEventSnapshot` with:

- `workerThreadTimeline` / `iocpThreadTimeline`
- `hillClimbing` (`ThreadPoolHillClimbingSample[]`)
- `workItemOrigins` (`ThreadPoolWorkItemOrigin[]`)
- `effectiveSettings` (`workerMinThreads`, `workerMaxThreads`, `iocpMinThreads`, `iocpMaxThreads`) when the runtime emits `ThreadPoolMinMaxThreadsChanged`
- `totalEnqueueEvents` / `totalDequeueEvents`
- `notes`

**Drilldown:** `query_snapshot(handle, view="summary" | "timeline" | "hillClimbing" | "workItemOrigins")`.

**Notes:**

- The runtime does not always publish named ThreadPool adjustment payloads on every platform; when that happens the collector annotates `notes` and infers the transition reason from the timing / direction of worker growth.
- Work-item origins require EventPipe call stacks on `ThreadPoolEnqueueWork`; when stacks are unavailable the collector returns a note and leaves `workItemOrigins` empty.
- Effective min/max counts are best-effort: the collector stays EventPipe-only and fills `effectiveSettings` only when the runtime emits `ThreadPoolMinMaxThreadsChanged`; otherwise it falls back to a note and points callers at `collect_thread_snapshot(view="threadpool")` for a ptrace-backed snapshot.
| `intervalSeconds` | `int` | `1` | Refresh interval requested from SqlClient EventCounters |
| `depth` | `SamplingDepth` | `Summary` | `Summary` keeps only the top command/N+1 slices inline; `Detail` / `Raw` keep the full capture |

**Returns:** `DbSnapshot` with:

- `totalCommands`
- `byCommand` (`DbCommandAggregate[]` with `commandTextHash`, sanitized SQL,
  sanitized connection string, `count`, `totalMs`, `maxMs`, `p95Ms`)
- `nPlusOne` (`DbNPlusOneIncident[]`)
- `connectionPool` (`DbConnectionPoolStats[]`)
- `notes`

**Drilldown:** `query_snapshot(handle, view="summary" | "byCommand" | "n+1" | "connectionPool")`.

**Notes:**

- `SensitiveDataRedactor` redacts connection-string secrets and inline SQL
  literal values before the snapshot is retained.
- SqlClient pool stats depend on provider support; when the target only emits EF
  activities the `connectionPool` slice may be empty.

---

## `collect_events(kind="event_source")`

> **Deprecated — call [`collect_events`](#collect_events) with `kind="event_source"`.**
> Behaviorally identical; will be removed in `0.7.0`.


Generic passthrough that opens an EventPipe session for any EventSource by
name and captures the events it emits in the window. Use for HTTP activity
(`System.Net.Http`), Kestrel/Hosting/Logging events, or app-defined sources.

**Parameters:**

| Name | Type | Default | Description |
|---|---|---|---|
| `processId` | `int` | — | Target process id |
| `providerName` | `string` | — | EventSource provider name. **Must be on the curated allowlist** (issue #165 / M2) — see [Security gates](#security-gates-b4); the deny path returns an `EventSourceProviderNotAllowed` envelope listing the curated set. |
| `durationSeconds` | `int` | `10` | Window length |
| `keywords` | `long` | `-1` | Keyword mask. `-1` = all (clamped to `0` for opt-in non-allowlisted providers when left at `-1`). |
| `eventLevel` | `int` | `5` | 0=LogAlways…5=Verbose (clamped to `4` for opt-in non-allowlisted providers when left above `4`). |
| `maxEvents` | `int` | `200` | Cap on captured events |
| `unsafeProvider` | `bool` | `false` | Opt-in for non-allowlisted providers (issue #165 / M2). Only honoured when the server has `Diagnostics:AllowSensitiveHeapValues=true`. |

**Returns:** `EventSourceCapture`:

```json
{
  "processId": 12345,
  "provider": "System.Net.Http",
  "startedAt": "2026-05-18T20:00:00Z",
  "duration": "00:00:10",
  "totalEvents": 128,
  "events": [
    {
      "timestamp": "2026-05-18T20:00:00.500Z",
      "provider": "System.Net.Http",
      "eventName": "RequestStart",
      "level": "Informational",
      "payload": { "scheme": "https", "host": "api.example.com", "port": "443" }
    }
  ]
}
```

**Tips:**

- `System.Net.Http` — outbound HTTP request/response timing
- `Microsoft.AspNetCore.Hosting` — request pipeline events
- `Microsoft-AspNetCore-Server-Kestrel` — connection lifecycle
- `Microsoft-Extensions-Logging` — structured app logs flowing through ILogger

---

## `collect_process_dump`

Writes a process dump to disk via the diagnostic IPC channel.

> **Defense in depth — `confirm=true` required (B5.6 / RFC 0001 §4).** Without
> `confirm=true` the tool returns a `{ "kind": "confirmation_required", ... }`
> envelope describing the dump that *would* have been written (`targetPid`,
> `dumpType`, `outputDirectory`) and writes nothing to disk. The
> `dump-write` + `ptrace` scopes are still required on top of `confirm=true`.
> Two-call pattern from an LLM:
>
> ```text
> # 1. Preview — no dump written.
> collect_process_dump(processId=12345, dumpType="WithHeap")
> # → { "kind": "confirmation_required", "targetPid": 12345, "dumpType": "WithHeap", ... }
>
> # 2. Surface the preview to a human, then re-issue with confirm=true.
> collect_process_dump(processId=12345, dumpType="WithHeap", confirm=true)
> # → { "kind": "dump_written", "dump": { "filePath": "...", ... } }
> ```

> **Sandbox (issue #163).** `outputDirectory` is interpreted as a **relative**
> sub-path under the operator-configured artifact root. The root is set by the
> `MCP_ARTIFACT_ROOT` environment variable (default
> `{TempPath}/dotnet-diagnostics-mcp`). Absolute paths, `..` traversal, and
> symlink escapes are rejected with a structured `InvalidArtifactPath` error.
> Files are written with POSIX mode `0600`; the parent directory is `0700`.

**Parameters:**

| Name | Type | Default | Description |
|---|---|---|---|
| `processId` | `int` | — | Target process id |
| `dumpType` | `string` | `"Mini"` | `Mini` / `Triage` / `WithHeap` / `Full` |
| `outputDirectory` | `string?` | artifact root | **Relative** sub-path under `MCP_ARTIFACT_ROOT`. Must not be absolute. |
| `confirm` | `bool` | `false` | **Required `true` to actually write the dump.** Without it, the tool returns a `confirmation_required` preview and writes nothing. See RFC 0001 §4. |

**Returns:** `DumpToolResult` — a discriminated envelope:

```json
// confirm=false (default) — no file written:
{
  "kind": "confirmation_required",
  "message": "collect_process_dump writes a heap dump to disk. Pass confirm=true to proceed.",
  "targetPid": 12345,
  "dumpType": "Mini",
  "outputDirectory": "dumps/oncall-20260518"
}

// confirm=true — file written:
{
  "kind": "dump_written",
  "targetPid": 12345,
  "dumpType": "Mini",
  "outputDirectory": "dumps/oncall-20260518",
  "dump": {
    "processId": 12345,
    "dumpType": "Mini",
    "filePath": "/tmp/dotnet-diagnostics-mcp/dumps/oncall-20260518/dump_pid12345_Mini_20260518T200000Z.dmp",
    "fileSizeBytes": 28311552,
    "createdAt": "2026-05-18T20:00:00Z"
  }
}
```

**Cost / size:**

| Type | Approx. size for a 200 MB workload | Use when |
|---|---|---|
| `Mini` | ~30 MB | crash triage, thread state |
| `Triage` | ~30 MB | minimal, strings stripped |
| `WithHeap` | full workload + heap (200+ MB) | leak/heap investigation |
| `Full` | largest | last resort, full address space |

**Side effects:** **writes to disk** on the server. In a sidecar topology the
file lives on the sidecar container's filesystem — mount a PVC if you expect
to capture more than transient dumps.

## `capture_method_bytes`

Reads the JIT-emitted (or ReadyToRun-baked) native machine code for a single
managed method out of a live .NET process (or `WithHeap`/`Full` dump) and
writes the raw bytes to a file on disk. Closes the only disasm coverage gap:
NativeAOT and R2R binaries live on disk and are already covered by
`dotnet-native-mcp`; JIT-emitted code lives only in the target process memory.

The bytes are emitted via a **file side-channel** (mirroring `collect_process_dump`)
so binary payloads never enter the LLM context. Each captured region returns a
`NextActionHint` for `dotnet-native-mcp.disassemble(rawBlob=true)` carrying the
file path, size, architecture and load-base — feed that hint verbatim to
disassemble.

**Backend:** ClrMD `HotColdInfo`. **Requires:** CoreCLR target (NativeAOT
returns an error envelope — use `dotnet-native-mcp.load_native_binary` against
the binary on disk instead). On Linux also requires `CAP_SYS_PTRACE` for live
attach.

**Parameters:**

| Name | Type | Default | Description |
|---|---|---|---|
| `moduleVersionId` | `string` (GUID) | — | MVID of the method's declaring module (from a sampler hotspot's `MethodIdentity`) |
| `metadataToken` | `string` | — | MethodDef token (`0x06000123` or decimal) |
| `processId` | `int?` | auto-select | Live PID. Mutually exclusive with `dumpFilePath` |
| `dumpFilePath` | `string?` | — | Path to a `WithHeap`/`Full` dump. Mutually exclusive with `processId` |
| `codeAddress` | `string?` | — | Optional native IP (hex or decimal) for the fast `GetMethodByInstructionPointer` path; verified against `(mvid, token)` |
| `tier` | `string?` | — | Informational label (`Tier0`/`Tier1`/etc.) echoed into the output file name. ClrMD does not expose tier metadata, so this is **not** a filter |
| `outputDirectory` | `string?` | `method-bytes/{pid}` | **Relative** sub-path under `MCP_ARTIFACT_ROOT` (default `{TempPath}/dotnet-diagnostics-mcp`). Same sandbox rules as `collect_process_dump`: absolute paths, `..` traversal, and symlink escapes are rejected with `InvalidArtifactPath`. `.bin` files are written `0600`. |

**Returns:** `CapturedMethodBytes`:

```json
{
  "origin": "Live",
  "processId": 12345,
  "runtimeName": "coreclr",
  "runtimeVersion": "10.0.0",
  "architecture": "X64",
  "method": { "moduleVersionId": "…", "metadataToken": 100663297, "methodName": "…", "typeFullName": "…" },
  "regions": [
    { "filePath": "/tmp/…/My.Type.Method-Hot--0x06000001.bin", "size": 412, "baseAddress": 140234567890, "architecture": "X64", "region": "Hot", "tier": null, "compilationType": "Jit" }
  ],
  "outputDirectory": "/tmp/…",
  "warnings": []
}
```

**Handoff:** every region carries a `NextActionHint` for
`dotnet-native-mcp.disassemble` with `imagePath`, `rawBlob: true`, `rva: 0`,
`size`, `architecture` and `baseAddress` — pass those through unchanged.

**Side effects:** writes one `.bin` file per region (Hot, plus Cold when the
JIT split the method). Suspend window on live attach is typically < 100 ms.
**NativeAOT/R2R targets are rejected** with an explanatory error envelope.

## `get_bytes`

**Successor (RFC 0002 §4.4) to `get_bytes(kind="module")` + `get_bytes(kind="dump")`.** Single
byte-fetch entrypoint that dispatches on a `kind` discriminator:

- `kind: "module"` — same shape as the legacy `get_bytes(kind="module")`. Required
  `moduleVersionId`; optional `asset` (`"pe"`/`"pdb"`), `processId`.
- `kind: "dump"` — same shape as the legacy `get_bytes(kind="dump")`. Required
  `dumpFilePath` (under `MCP_ARTIFACT_ROOT`).

Both branches share `offset` / `maxBytes` and return the same
`ByteFetchEnvelope` documented below. Unknown `kind` returns a structured
`InvalidArgument` error envelope listing the allowed values — never throws.

> **Scope:** `module-bytes-read` (literal modifier — same enforcement as the
> legacy tools).

The legacy `get_bytes(kind="module")` and `get_bytes(kind="dump")` entrypoints remain available
during the deprecation window and emit byte-for-byte identical envelopes — see
`GetBytesCompatibilityTests` for the asserted contract.

## `get_bytes(kind="module")`

Streams a loaded managed module's PE or PDB in repeated `CallTool` chunks so a
client-side sibling MCP can materialize the bytes locally in orchestrator mode.
The tool resolves the module by **MVID** inside a live process, then returns a
`ByteFetchEnvelope` carrying the full-artifact SHA-256, the current chunk, and a
`NextActionHint` for the follow-up `offset` call when more bytes remain.

> **Scope:** `module-bytes-read` is a **literal modifier scope**. A root/`*`
> bearer passes the outer `[RequireScope]` gate but is still rejected in-method
> unless the token literally carries `module-bytes-read`.

**Parameters:**

| Name | Type | Default | Description |
|---|---|---|---|
| `moduleVersionId` | `string` (GUID `D`) | — | MVID of the loaded module to stream |
| `asset` | `string` | `"pe"` | `"pe"` or `"pdb"` |
| `offset` | `long` | `0` | Chunk start offset |
| `maxBytes` | `int` | `4_194_304` | Requested chunk size; capped at `16 MiB` |
| `processId` | `int?` | auto-select | Live PID. Omit to use the normal resolver |

**Returns:** `ByteFetchEnvelope`:

```json
{
  "kind": "module",
  "asset": "pe",
  "identifier": "6f5c9bf0-1e0b-4f3b-9a8e-...",
  "sourcePath": "/app/MyService.dll",
  "totalSize": 1835008,
  "sha256": "4d9d...",
  "offset": 0,
  "chunkSize": 4194304,
  "base64Chunk": "TVqQ...",
  "nextOffset": 4194304,
  "companionPdbPath": "/app/MyService.pdb",
  "pdbIsEmbedded": null,
  "processId": 12345
}
```

**When to use:** cross-MCP handoff in orchestrator mode when `dotnet-assembly-mcp`
or `dotnet-native-mcp` cannot be co-located with the diagnostics sidecar.

**When NOT to use:** local / twin-sidecar topologies where the sibling MCP can
already see the pod-local filesystem directly.

## `get_bytes(kind="dump")`

Streams a dump file already living under `MCP_ARTIFACT_ROOT` (or an absolute
path that still resolves under that root after symlink resolution). The shape is
identical to `get_bytes(kind="module")`, but the `asset` is always `"dump"` and the
`identifier` is the canonical dump path under the sandbox.

> **Scope:** same literal `module-bytes-read` requirement as `get_bytes(kind="module")`.

**Parameters:**

| Name | Type | Default | Description |
|---|---|---|---|
| `dumpFilePath` | `string` | — | Relative path under `MCP_ARTIFACT_ROOT`, or an absolute path that still resolves under that root |
| `offset` | `long` | `0` | Chunk start offset |
| `maxBytes` | `int` | `4_194_304` | Requested chunk size; capped at `16 MiB` |

**Notes:**

- `dumpFilePath` is re-validated on **every** call via the artifact-root sandbox.
  `..`, symlink escape, and absolute paths outside the root return
  `InvalidArtifactPath`.
- Artifacts larger than `256 MiB` are rejected with `InvalidArgument` rather
  than partially streamed.
- The returned `sha256` is for the **entire** dump, not just the current chunk.

**When to use:** after `collect_process_dump(confirm=true)` when a client-side
sibling MCP needs the dump bytes locally.

**When NOT to use:** as a generic file reader — the sandbox intentionally only
covers dump artifacts under `MCP_ARTIFACT_ROOT`.

---

## `list_orchestrator`

RFC 0002 §4.7 consolidation of the orchestrator listing surface (issue #212). One
read-only tool that dispatches on `kind`:

| `kind` | Replaces | Required scope | Returns |
|---|---|---|---|
| `pods` | `list_orchestrator(kind="pods")` | `orchestrator-list` | `PodCandidatePage` under `data.pods` |
| `investigations` | `list_orchestrator(kind="investigations")` | `orchestrator-attach` | `InvestigationListPage` under `data.investigations` |

Per-kind parameters are preserved verbatim:

- **`kind="pods"`** — `namespace`, `labelSelector`, `fieldSelector`,
  `containerName`, `preparedOnly` (default `true`), `includeNotReady`
  (default `false`), `limit` (default `100`, clamped to
  `Orchestrator:MaxListLimit`), `cursor`.
- **`kind="investigations"`** — `includeTerminal` (default `false`),
  `includeAllSessions` (default `false`; requires
  `Orchestrator:AllowCrossSessionAdmin=true` **or** the bearer's
  `orchestrator-admin` modifier scope).

**Result envelope:**

```json
{
  "summary": "...",
  "hints": [ ... ],
  "data": {
    "kind": "pods",                  // discriminator echo
    "pods":            { "items": [...], "nextCursor": null },   // when kind=pods
    "investigations":  null                                       // null when not selected
  }
}
```

Exactly one of `data.pods` / `data.investigations` is populated, matching `data.kind`.
Errors (unknown `kind`, orchestrator disabled, scope mismatch) surface as the
standard `DiagnosticError` envelope with kinds `InvalidArgument`,
`OrchestratorDisabled`, or `PermissionDenied` respectively.

**Authorization.** The MCP scope filter accepts either of `orchestrator-list` /
`orchestrator-attach`. The tool re-checks scopes per `kind` so a token holding
only `orchestrator-list` cannot enumerate investigation handles by switching the
discriminator (RFC §4.7).

**Why `attach_to_pod` / `detach_from_pod` are NOT folded in.** RFC §4.7 — those
verbs have side-effect boundaries (ephemeral-container injection, handle close,
session unbind) that are distinct from read-only listing. They remain explicit.

**Deprecation.** `list_orchestrator(kind="pods")` and `list_orchestrator(kind="investigations")` are still
registered and behave unchanged, but each carries `[DeprecatedTool]` metadata
pointing at `list_orchestrator` and will be removed in **0.7.0**.

**Examples**

```jsonc
// Enumerate prepared Pods in a namespace:
{ "name": "list_orchestrator", "arguments": {
    "kind": "pods", "namespace": "checkout", "labelSelector": "app=api" } }

// List active handles for the current MCP session:
{ "name": "list_orchestrator", "arguments": {
    "kind": "investigations", "includeTerminal": false } }
```

---

## `discover_azure`

Azure discovery v1 (issue #232, parent #230). Single `kind`-discriminated tool that
enumerates .NET workload candidates in an Azure subscription across three platforms.

| `kind` | Required scope | Returns |
|---|---|---|
| `webapps` (default) | `azure-discovery` | `AzurePagedResult<AzureWebAppCandidate>` under `data.webapps` |
| `containerapps` | `azure-discovery` | `AzurePagedResult<AzureContainerAppCandidate>` under `data.containerapps` |
| `aksclusters` | `azure-discovery` | `AzurePagedResult<AzureAksClusterCandidate>` under `data.aksclusters` |

**Parameters**

- `subscriptionId` *(required)* — Azure subscription id (string GUID).
- `kind` — discriminator, see table above. Case-sensitive.
- `resourceGroup` — optional resource-group filter; null lists across the whole subscription.
- `includeStopped` *(default false)* — when true, backends include stopped / failed resources.
- `limit` *(default 100)* — page size; clamped to `200`.
- `cursor` — opaque continuation token from a prior page; null for the first page.
- `includeKubeconfig` *(default false)* — `aksclusters` only. When true, the AKS
  backend returns an opaque kubeconfig handle (`AzureAksHandoff`) — never raw
  kubeconfig content.

**Result envelope**

```json
{
  "summary": "...",
  "hints": [],
  "data": {
    "kind": "containerapps",
    "webapps":        null,
    "containerapps":  { "items": [...], "nextCursor": null },
    "aksclusters":    null
  }
}
```

Exactly one of `data.webapps` / `data.containerapps` / `data.aksclusters` is populated,
matching `data.kind`. Errors (missing subscription id, unknown `kind`, Azure discovery
disabled, scope mismatch) surface as the standard `DiagnosticError` envelope with kinds
`InvalidArgument`, `AzureDiscoveryDisabled`, or `PermissionDenied` respectively.

**Registration.** Gated on the `AzureDiscovery:Enabled` configuration flag — a server
with the master switch off looks identical to a pre-#232 build (the tool is not
registered and the Azure SDK is never reached).

**Backends.** The contract is shipped in #232; the real backends arrive in:
- **#233** — App Service (`webapps`) + Container Apps (`containerapps`).
- **#234** — AKS (`aksclusters`), including the kubeconfig-handle store.

Until those PRs merge, calling the tool with `AzureDiscovery:Enabled=true` throws
`NotImplementedException` through the backend stubs. See
[`docs/azure-discovery.md`](./azure-discovery.md) for the full design.

---

## Security gates (B4)

Issue #165 introduced three opt-in security gates that change the default behaviour of
`query_snapshot`, `collect_events(kind="event_source")` and `collect_sample(kind="cpu")`. All three are bound
from the `Diagnostics:` configuration section and can be set via env vars
(`Diagnostics__AllowSensitiveHeapValues=true`, `Diagnostics__EventSourceAllowlist__0=…`,
`Diagnostics__SymbolServerAllowlist__0=msdl.microsoft.com`).

> **B5.4 — modifier scopes preferred.** All three gates now accept an RFC 0001 modifier
> scope on the bearer principal as an alternative authorisation path:
> `sensitive-heap-read`, `eventsource-any`, `symbols-remote`. The scope-first predicate is
> `principal.HasExplicitScope("<scope>") OR <legacy-flag-or-allowlist-allows>` — either
> path is sufficient, so existing deployments keep working. The legacy paths now emit a
> once-per-process deprecation warning when they are the mechanism that unlocked the call.
>
> Scope membership is **literal**: a `root`/`*` token does **not** auto-grant the modifier
> scopes (this preserves least-surprise for the SSRF / sensitive-data gates — operators
> must deliberately mint a scoped token). The
> `Diagnostics:EventSourceAllowlist` and `Diagnostics:SymbolServerAllowlist` policies
> themselves are **retained** as fallback value-shaping. Only
> `Diagnostics:AllowSensitiveHeapValues` is slated for removal in a future release —
> prefer minting a token with the `sensitive-heap-read` scope today.

### H4 — heap drilldown defaults to metadata-only

`query_snapshot` with `view=duplicate-strings` and `view=object` no longer returns raw
string previews or field/array element values by default. Instead each value site is replaced
with `<redacted:metadata-only>` and the LLM gets length / type / address metadata only.

To opt-in (**scope-first path, recommended**):

1. mint a bearer token with the `sensitive-heap-read` scope (RFC 0001 §2.3 — see
   `deploy/helm/README.md` for the chart-level shape), **and**
2. pass `includeSensitiveValues=true` on the per-call invocation.

Legacy fallback (deprecated — emits a once-per-process warning):

1. set `Diagnostics:AllowSensitiveHeapValues=true` on the server, **and**
2. pass `includeSensitiveValues=true` on the per-call invocation.

When the gate opens via either path, values flow through `SensitiveDataRedactor`, which
replaces any substring matching the default patterns (Bearer/Basic tokens, JWT-shaped
triples, `password=`/`secret=`/`api_key=` query-string syntax, AWS access keys, GitHub PATs,
PEM blocks) with `<redacted:sensitive>`. Add custom patterns via
`Diagnostics:RedactionPatterns[]`.

The `heap-snapshot://` MCP resource projection is **always metadata-only** — it has no
per-call opt-in surface, so neither the scope nor the server flag can unlock raw values
through that path. Operators who need the redacted-but-present view should call
`query_snapshot view=duplicate-strings includeSensitiveValues=true` (which honours
both gates).

### M2 — `collect_events(kind="event_source")` provider allowlist

Arbitrary user-defined EventSource providers were the easiest way for an attacker who
gained MCP access to siphon application-defined logging (which routinely contains tokens,
PII, SQL parameters). The tool now refuses any `providerName` that is not on the curated
default allowlist (System.Net.Http, Microsoft.AspNetCore.Hosting,
Microsoft-AspNetCore-Server-Kestrel, Microsoft-Extensions-Logging,
Microsoft-Windows-DotNETRuntime, System.Threading.Tasks.TplEventSource, …) or under
`Diagnostics:EventSourceAllowlist[]`.

To capture a custom provider:

- **Scope-first path (recommended).** Grant the bearer the `eventsource-any` scope; the
  tool will then accept any `providerName` regardless of the curated allowlist when the
  caller passes `unsafeProvider=true`. The keyword/level clamping below still applies.
- Add the provider to `Diagnostics:EventSourceAllowlist[]` (preferred over the legacy
  flag — survives across calls). When a call is authorised by the allowlist alone (no
  `eventsource-any` scope on the bearer) the tool emits a once-per-process deprecation
  warning so operators see they should be distinguishing callers with scopes rather
  than relying on a deployment-wide allowlist.
- Legacy fallback (deprecated — emits a once-per-process warning): set
  `Diagnostics:AllowSensitiveHeapValues=true` on the server **and** pass
  `unsafeProvider=true` on the call.

On any `unsafeProvider=true` path `keywords=-1` is clamped to `0` and `eventLevel>4` is
clamped to `Informational` unless the caller passed explicit safer values.

### M3 — symbol-server SSRF guard

`symbolPath` historically accepted any `srv*http(s)://…` segment, which let a malicious
caller turn the sidecar into an outbound HTTP client to any host on the cluster network.
Caller-supplied `symbolPath` values are now parsed and every `srv*` / `symsrv*` segment's
`http://` / `https://` URL must host-match `Diagnostics:SymbolServerAllowlist[]`, **or**
the principal must hold the `symbols-remote` modifier scope (scope-first path —
recommended). Local filesystem paths and bare directory entries always pass through. The
deny path returns a `SymbolServerNotAllowed` envelope. When a call is authorised by the
allowlist alone (no `symbols-remote` scope on the bearer) the tool emits a once-per-process
deprecation warning. Tools covered:

- `collect_sample(kind="cpu")`
- `collect_sample(kind="off_cpu")`
- `collect_thread_snapshot`
- `inspect_heap` (and its deprecated aliases `inspect_heap(source="dump")` / `inspect_heap(source="live")`)

`MCP_SYMBOL_PATH` and `_NT_SYMBOL_PATH` from the **operator-set environment** are *not*
validated — they are treated as trusted by the deployment.
