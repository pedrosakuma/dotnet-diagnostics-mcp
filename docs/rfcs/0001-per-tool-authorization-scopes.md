# RFC 0001 — Per-tool authorization scopes

- **Audit batch:** B5 (gpt-5.5 security audit, finding H3)
- **Tracking issue:** [#166](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/issues/166)
- **Status:** Approved; B5.1 (foundation) and B5.2 (per-tool decoration + enforcement filter) shipped. Per-handle §2.12 enforcement deferred — `query_collection` is the lone tool with a coarser `RequireAnyScope` approximation pending handle-store work.
- **Depends on:** B3 (issue #164 — investigation proxy), B4 (issue #165 — heap secret/event source/symbol allowlists)
- **Author:** Copilot (drafting on behalf of @pedrosakuma)

## 1. Context

### 1.1 Current state

The MCP server exposes **34 tools** today, every one of them gated by a *single*
`MCP_BEARER_TOKEN`. Enumeration from `src/DotnetDiagnosticsMcp.Server/Tools/`:

`DiagnosticTools.cs` (30 tools). Line numbers are anchored to commit `292264e`; treat
the table here as the authoritative inventory and the per-scope tables in §2 as views
over it. If `[McpServerTool]` decorations move, only this section must be re-pinned.

| Tool | file:line |
|---|---|
| `list_dotnet_processes` | DiagnosticTools.cs:40 |
| `get_process_info` | DiagnosticTools.cs:72 |
| `get_diagnostic_capabilities` | DiagnosticTools.cs:110 |
| `get_container_signals` | DiagnosticTools.cs:157 |
| `get_memory_trend` | DiagnosticTools.cs:263 |
| `snapshot_counters` | DiagnosticTools.cs:348 |
| `collect_cpu_sample` | DiagnosticTools.cs:435 |
| `collect_allocation_sample` | DiagnosticTools.cs:569 |
| `get_call_tree` | DiagnosticTools.cs:647 |
| `collect_off_cpu_sample` | DiagnosticTools.cs:709 |
| `query_off_cpu_snapshot` | DiagnosticTools.cs:828 |
| `query_collection` | DiagnosticTools.cs:935 |
| `collect_exceptions` | DiagnosticTools.cs:1071 |
| `collect_gc_events` | DiagnosticTools.cs:1142 |
| `collect_activities` | DiagnosticTools.cs:1209 |
| `collect_event_source` | DiagnosticTools.cs:1272 |
| `collect_process_dump` | DiagnosticTools.cs:1362 |
| `inspect_dump` | DiagnosticTools.cs:1404 |
| `inspect_live_heap` | DiagnosticTools.cs:1500 |
| `query_heap_snapshot` | DiagnosticTools.cs:1569 |
| `collect_thread_snapshot` | DiagnosticTools.cs:2006 |
| `capture_method_bytes` | DiagnosticTools.cs:2139 |
| `get_module_bytes` | DiagnosticTools.cs:2412 |
| `get_dump_bytes` | DiagnosticTools.cs:2476 |
| `query_thread_snapshot` | DiagnosticTools.cs:2285 |
| `start_investigation` | DiagnosticTools.cs:2465 |
| `export_investigation_summary` | DiagnosticTools.cs:2519 |
| `compare_to_baseline` | DiagnosticTools.cs:2582 |

`OrchestratorTools.cs` (4 tools):

| Tool | file:line |
|---|---|
| `list_pods` | OrchestratorTools.cs:30 |
| `attach_to_pod` | OrchestratorTools.cs:139 |
| `detach_from_pod` | OrchestratorTools.cs:325 |
| `list_active_investigations` | OrchestratorTools.cs:430 |

The single bearer is parsed in
`src/DotnetDiagnosticsMcp.Server/Auth/BearerTokenMiddleware.cs:14-39`. There is no
authorization layer between "request authenticated" and "tool handler invoked".

### 1.2 Privileged primitives behind the bearer

The same key unlocks operations whose blast radius differs by orders of magnitude:

- **`CAP_SYS_PTRACE`** — required by `collect_thread_snapshot`, `inspect_live_heap`,
  `capture_method_bytes`, and `collect_process_dump`. On Debian/Ubuntu hosts with
  `kernel.yama.ptrace_scope=1` same-UID attach is blocked without this capability;
  the K8s sidecar adds it via `securityContext.capabilities.add: ["SYS_PTRACE"]`.
- **Matching UID** — every tool that opens the diagnostic IPC socket
  (`/tmp/dotnet-diagnostic-<pid>`) inherits the target app's UID. The sidecar runs as
  the same UID as the target by design.
- **Ephemeral container injection** — `attach_to_pod` (OrchestratorTools.cs:139) creates
  an ephemeral debug container in the target pod via the Kubernetes API.
- **Dump-write** — `collect_process_dump` writes a `.dmp` file (potentially gigabytes)
  to a sandboxed directory; the file contains every secret resident in memory.
- **JIT byte capture** — `capture_method_bytes` reads JIT-emitted machine code out of a
  live process's code heap.

A caller in possession of the bearer can perform any of these actions, regardless of
what they were notionally hired to do.

### 1.3 What B3 + B4 already shipped as proto-gates

Several recent merges introduced *ad-hoc* opt-in flags that this RFC subsumes:

| Flag | Defined at | Subsumed by scope |
|---|---|---|
| `Orchestrator:AllowCrossSessionAdmin` | `src/DotnetDiagnosticsMcp.Server/Orchestrator/OrchestratorOptions.cs:140` (used at `OrchestratorTools.cs:384, 466`, `Hosting/InvestigationProxyEndpoints.cs:161`) | `orchestrator-admin` |
| `Diagnostics:AllowSensitiveHeapValues` | `src/DotnetDiagnosticsMcp.Core/Security/SecurityOptions.cs:32` (used by `SensitiveValueGate.cs:16`, `collect_event_source` per `DiagnosticTools.cs:1316`) | `sensitive-heap-read` |
| `Diagnostics:EventSourceAllowlist` | `src/DotnetDiagnosticsMcp.Core/Security/SecurityOptions.cs:35` (used by `EventSourceAllowlist.cs:42`) | **Kept** — an allowlist is a content filter, not a scope. Scope `eventpipe` still required to call `collect_event_source`. |
| `Diagnostics:SymbolServerAllowlist` | `src/DotnetDiagnosticsMcp.Core/Security/SecurityOptions.cs:39` (used by `SymbolServerAllowlist.cs:20`) | **Kept** — same rationale: an SSRF host filter, orthogonal to scope. |

The `collect_event_source` help text already advertises the future state — see
`DiagnosticTools.cs:1316` ("the per-tool scope mechanism in #166 (B5) will replace this
flag").

The B1/H9 non-loopback startup guard (`src/DotnetDiagnosticsMcp.Server/Program.cs:97-113`)
is the foundation this RFC builds on: networked deployments are already required to
configure an operator-managed bearer; this RFC tightens that bearer into a scope set.

### 1.4 Threat model

An adversary who **obtains a token** (leaked through a CI log, a misconfigured Secret, a
forgotten `kubectl exec ... env`, or an over-permissive PVC) should be capped at the
minimum scope the holder of that token was granted. Today a leaked token is equivalent
to root on the diagnostic surface of the target process; that is unacceptable for the
multi-tenant K8s investigation deployments tracked by the orchestrator design.

## 2. Proposed scope taxonomy

Names are kebab-case, stable, never renamed. Each scope is a distinct trust boundary —
the privilege rationale section justifies why each split exists.

### 2.1 `read-counters`

Cheap, read-only, low-cardinality output. Includes the lightweight EventCounters
listener (`snapshot_counters`) because its payload is aggregated numeric counters; it
does *technically* open an EventPipe session, but the produced data is bounded
(provider × counter name × value) and is materially less sensitive than the per-event
streams covered by `eventpipe` in §2.2. No `ptrace`.

| Tool | file:line |
|---|---|
| `list_dotnet_processes` | DiagnosticTools.cs:40 |
| `get_process_info` | DiagnosticTools.cs:72 |
| `get_diagnostic_capabilities` | DiagnosticTools.cs:110 |
| `get_container_signals` | DiagnosticTools.cs:157 |
| `get_memory_trend` | DiagnosticTools.cs:263 |
| `snapshot_counters` | DiagnosticTools.cs:348 |

**Note on `snapshot_counters` and `query_collection`.** `snapshot_counters` mints a
handle of kind `Counters` (`DiagnosticTools.cs:347-430`) that callers drill into via
`query_collection`, which the taxonomy primarily places under `eventpipe` (§2.2).
This is reconciled by the **per-handle-kind authorization rule** described in §2.12:
`query_collection` authorizes against the *originating* tool's scope set, recorded in
the handle metadata at mint time. A holder of `read-counters` alone can query the
counter handles they minted; querying a `Cpu`/`GcEvents`/`EventSource` handle requires
`eventpipe`.

**Privilege rationale.** Aggregated counters / `/proc` reads only. Distinct boundary
because a dashboard with no business reading exception messages or allocation stacks
should be denied them.

**Risk if leaked.** Adversary can enumerate processes, read counter cardinality (low
signal), and observe RSS/PSS curves. No bytes from the target heap escape.

### 2.2 `eventpipe`

EventPipe sessions plus the read-only handles their collectors mint. `query_*` tools
authorize per the **handle-ownership rule** in §2.12: a query is allowed only when the
caller's scope set is a superset of the scope set the originating collector required at
mint time.

| Tool | file:line |
|---|---|
| `collect_cpu_sample` | DiagnosticTools.cs:435 |
| `collect_allocation_sample` | DiagnosticTools.cs:569 |
| `collect_off_cpu_sample` | DiagnosticTools.cs:709 |
| `query_off_cpu_snapshot` | DiagnosticTools.cs:828 |
| `query_collection` | DiagnosticTools.cs:935 *(also satisfied by `read-counters` — see §2.12)* |
| `collect_exceptions` | DiagnosticTools.cs:1071 |
| `collect_gc_events` | DiagnosticTools.cs:1142 |
| `collect_activities` | DiagnosticTools.cs:1209 |
| `collect_event_source` | DiagnosticTools.cs:1272 |

**Privilege rationale.** EventPipe is in-process Microsoft.Diagnostics.NETCore.Client
machinery; it does not require `CAP_SYS_PTRACE` and does not write artifacts to disk.
But it *does* expose exception messages, allocation type names, and user-defined
EventSource payloads, all of which can contain secrets or PII. Distinct from
`read-counters` because the data is per-event, not aggregated.

**Risk if leaked.** Adversary can observe exception messages (PII / DSN-class leaks),
SQL/HTTP activities (parameters can include credentials), and allocation type sites
(internal class names, design leakage). Cannot mutate target state.

### 2.3 `heap-read`

Read-only heap walks. `inspect_live_heap` additionally requires the `ptrace` scope (it
suspends the target and walks via ClrMD's data-target reader); see §2.5.

| Tool | file:line | Notes |
|---|---|---|
| `inspect_dump` | DiagnosticTools.cs:1404 | Reads a `.dmp` from disk. |
| `inspect_live_heap` | DiagnosticTools.cs:1500 | **Requires `heap-read` *and* `ptrace`.** |
| `query_heap_snapshot` | DiagnosticTools.cs:1569 | Drilldown over a handle minted by either of the above. |

**Privilege rationale.** Heap walks return type-rooted graphs with object addresses,
retention chains, and (when `sensitive-heap-read` is also granted) string contents.
Strictly more sensitive than EventPipe because every byte resident in managed memory is
in reach. The strawman in #166 used a single `heap-read` for both live and dump; this
RFC keeps that and stacks `ptrace` on the *live* variant rather than inventing a
`heap-read-live` synonym. Stacking is collectively exhaustive — see §2.14.

**Risk if leaked.** Adversary can enumerate every managed type, read retention chains,
and (with `sensitive-heap-read`) read every string interned in the heap — passwords,
tokens, request bodies, mailboxes.

### 2.4 `sensitive-heap-read`

Replaces `Diagnostics:AllowSensitiveHeapValues`. Only meaningful when stacked with
`heap-read` or `eventpipe`. Unlocks string-content surfaces that are currently gated by
`SensitiveValueGate` (`src/DotnetDiagnosticsMcp.Core/Security/SensitiveValueGate.cs:16`)
and the `unsafeProvider=true` switch on `collect_event_source`
(`DiagnosticTools.cs:1297`).

**Privilege rationale.** This is the "I accept that strings leave this process verbatim"
opt-in. Separating it from `heap-read` lets operators grant heap topology (useful for
leak triage) without granting heap contents.

**Risk if leaked.** Full plaintext access to every string the target has interned or
allocated since the EventPipe session opened. Worst case for confidentiality short of a
full memory dump.

### 2.5 `ptrace`

Operations that depend on `CAP_SYS_PTRACE` (Linux) or its equivalent (Windows debug
privilege).

| Tool | file:line | Notes |
|---|---|---|
| `collect_thread_snapshot` | DiagnosticTools.cs:2006 | Read-only; suspends briefly. |
| `query_thread_snapshot` | DiagnosticTools.cs:2285 | Drilldown over the handle. |
| `capture_method_bytes` | DiagnosticTools.cs:2139 | Reads JIT machine code from the target's code heap. |
| `inspect_live_heap` | DiagnosticTools.cs:1500 | **Stacks with `heap-read` (see §2.3).** |

**Privilege rationale.** `ptrace` access is the precondition for arbitrary read of
another process's address space on the host. Even when the operation is logically
read-only, the *capability* is the security boundary that matters — a holder of the
`ptrace` scope is one bug away from `PTRACE_POKEDATA`.

**Risk if leaked.** Adversary can suspend the target (DoS), read any thread stack, and
extract JIT-emitted code (which can leak optimization decisions, embedded constants,
and indirectly some secrets baked into JIT'd literals).

### 2.6 `dump-write`

| Tool | file:line | Notes |
|---|---|---|
| `collect_process_dump` | DiagnosticTools.cs:1362 | **Requires `dump-write` *and* `ptrace`.** |

**Privilege rationale.** Writes a multi-gigabyte artifact to disk containing the
*entire* address space. The dump path goes through `createdump` / `MiniDumpWriteDump`,
which on Linux uses `ptrace`-class syscalls — so `dump-write` stacks on top of `ptrace`
(this is the second stacking case, alongside `inspect_live_heap` in §2.3). Every other
scope above is bounded by what the tool can serialize through the MCP envelope (capped,
redacted by `SensitiveDataRedactor` unless `sensitive-heap-read` is granted); a dump is
bounded only by filesystem quota and contains zero redaction. **This is the single most
dangerous scope.**

**Risk if leaked.** Complete in-memory state of the target persisted as a file the
operator must later transport, store, and eventually delete. See §4 for the additional
per-call `confirm=true` requirement.

### 2.7 `orchestrator-list`

| Tool | file:line |
|---|---|
| `list_pods` | OrchestratorTools.cs:30 |

**Privilege rationale.** Enumerates pods the orchestrator's `KubernetesPodAttachOrchestrator`
is allowed to see (already filtered by `Orchestrator:AllowedNamespaces`). Pure discovery;
no attach side effect.

**Risk if leaked.** Information disclosure — names of pods, containers, and the
`.NET / NativeAOT` capability of each. No mutation.

### 2.8 `orchestrator-attach`

| Tool | file:line |
|---|---|
| `attach_to_pod` | OrchestratorTools.cs:139 |
| `detach_from_pod` | OrchestratorTools.cs:325 |
| `list_active_investigations` | OrchestratorTools.cs:430 *(own-session only — see `orchestrator-admin` below)* |

**Privilege rationale.** Mutating Kubernetes API calls that create ephemeral debug
containers. The blast radius is bounded by the orchestrator service account's RBAC, but
within that radius a holder can inject a sidecar into any reachable pod.

**Risk if leaked.** Adversary can inject an ephemeral container, which on most clusters
inherits the pod's volumes and network namespace.

### 2.9 `orchestrator-admin`

Replaces `Orchestrator:AllowCrossSessionAdmin`. Required to list or operate on
investigation handles minted by *other* MCP sessions (see
`OrchestratorTools.cs:384, 466` and `Hosting/InvestigationProxyEndpoints.cs:161`).

**Privilege rationale.** Cross-session visibility is an operator-only privilege: it
breaks the tenant isolation that the single-session default provides. Modelling it as a
scope rather than a global flag means a "platform operator" token can be issued
distinct from an "investigator" token in the same deployment.

**Risk if leaked.** Adversary sees and can detach every other tenant's active
investigation.

### 2.9.1 `module-bytes-read`

Literal modifier scope for the cross-MCP byte-fetch tools.

| Tool | file:line | Notes |
|---|---|---|
| `get_module_bytes` | DiagnosticTools.cs:2412 | Streams PE / PDB bytes for a loaded managed module. |
| `get_dump_bytes` | DiagnosticTools.cs:2476 | Streams dump bytes under the artifact-root sandbox. |

**Privilege rationale.** These tools bridge pod-local binaries / dumps out of the
orchestrator boundary so sibling MCPs can materialize them client-side. The
payloads are raw executable / debug / dump bytes, not redacted summaries, so the
scope is deliberately **literal** (`HasExplicitScope`) and is never auto-granted
by `root` / `*`.

**Risk if leaked.** Adversary can export managed binaries, symbol files, and
suitably small dump files (`<= 256 MiB`) from the target environment.

### 2.10 `investigation-export`

| Tool | file:line |
|---|---|
| `start_investigation` | DiagnosticTools.cs:2465 |
| `export_investigation_summary` | DiagnosticTools.cs:2519 |
| `compare_to_baseline` | DiagnosticTools.cs:2582 |
| `get_call_tree` | DiagnosticTools.cs:647 |

**Privilege rationale.** Read-only meta-tools — they consult planning state, emit
JSON/Markdown summaries, and steer
investigation drilldowns the caller already minted. No new
collection is performed. The bucket also covers `get_call_tree`, which is a pure
drilldown over an already-collected CPU sample handle (and so authorizes per the
handle-ownership rule in §2.12 once that lands). Distinct scope because the *contents*
of an investigation summary include the findings of every collector run during the
session — granting export without `read-counters` makes little sense, but operators
may want to gate export separately (e.g. forbid customer-support roles from emitting
summaries containing exception text).

**Risk if leaked.** Adversary can re-export prior findings (already in their possession
if they were the original collector). Low
marginal risk; included for completeness.

### 2.11 `job-control` *(reserved — no tools currently assigned)*

The strawman in #166 proposed a dedicated `job-control` bucket for the legacy
`get_collection_status` / `cancel_collection` MCP surfaces. Those tools were retired
in RFC 0002 Stage B (issue #211) in favour of MCP-native progress + cancellation, so
no production tool maps to this scope today. The name is reserved
here so a future PR can introduce it cleanly the moment a job-control-only surface
appears (e.g. a watchdog token that can cancel but not enumerate prior findings).

### 2.12 Handle ownership and query authorization

Several drilldown tools (`query_collection`, `query_heap_snapshot`, `query_thread_snapshot`,
`query_off_cpu_snapshot`, `get_call_tree`) read from handles minted by other tools. The
current `MemoryDiagnosticHandleStore` is a process-wide singleton with no per-token or
per-scope metadata, so a "the handle is bound to a session" assumption is **not** true
of the codebase today.

**Rule.** A query against a handle is authorized iff the caller's scope set is a
superset of the *minting* tool's scope set, recorded in the handle metadata at mint
time as `RequiredScopes: ImmutableHashSet<string>`. Concretely:

| Minting tool | Recorded `RequiredScopes` | Authorized querier needs |
|---|---|---|
| `snapshot_counters` | `{read-counters}` | `read-counters` (or any superset) |
| `collect_cpu_sample` / `collect_gc_events` / `collect_exceptions` / `collect_event_source` / `collect_off_cpu_sample` / `collect_allocation_sample` / `collect_activities` | `{eventpipe}` | `eventpipe` |
| `inspect_dump` | `{heap-read}` | `heap-read` |
| `inspect_live_heap` | `{heap-read, ptrace}` | `heap-read` **and** `ptrace` |
| `collect_thread_snapshot` | `{ptrace}` | `ptrace` |

This closes two attack paths that a "primary scope on the query tool" model would leave
open:

1. A leaked handle from `inspect_live_heap` (which required `ptrace`) cannot be read by
   a token holding only `heap-read`.
2. A leaked handle from a privileged collector cannot be re-queried by a less-scoped
   token even within the same process.

**Implementation note.** B5.1 (§9) must extend `IDiagnosticHandleStore.Register` to
accept a `RequiredScopes` parameter and surface it on `TryGet`; the `RequireScopeAttribute`
applied to query tools must consult that metadata rather than only the static
`[RequireScope("…")]` attribute. The static attribute on a query tool declares the
*minimum* required scope (the most permissive originating collector) and the dynamic
check enforces the actual handle's required set. Filed as part of B5.1.

### 2.13 The `*` (root) pseudo-scope

A token granted `*` resolves to the union of every scope above. Used by:

- The legacy `MCP_BEARER_TOKEN` env var (back-compat — see §7).
- stdio mode and loopback HTTP mode by default (see §5).

### 2.14 Coverage check

Every `[McpServerTool]` decoration in `src/DotnetDiagnosticsMcp.Server/Tools/` is
assigned to exactly one *primary* scope (the scope a caller minimally needs).
`inspect_live_heap` additionally requires `ptrace` (§2.3) and `collect_process_dump`
additionally requires `ptrace` (§2.6). `query_collection` is the lone tool with
`RequireAnyScope("read-counters", "eventpipe")` semantics (handle-ownership
approximation pending §2.12 — see also §2.15). Coverage as implemented by B5.2:

```
6  read-counters
8  eventpipe
2  heap-read   (inspect_live_heap also requires ptrace)
1  dump-write  (collect_process_dump also requires ptrace)
3  ptrace
1  any-of(read-counters, eventpipe)   query_collection
6  investigation-export
1  orchestrator-list
3  orchestrator-attach
2  module-bytes-read
0  job-control (reserved, see §2.11)
---
34 tools total (30 in DiagnosticTools.cs + 4 in OrchestratorTools.cs)
```

`sensitive-heap-read`, `eventsource-any`, `symbols-remote`, `orchestrator-admin`,
and `module-bytes-read` are *modifier* scopes checked via
`BearerPrincipal.HasExplicitScope` (no wildcard honour) so a root token does not
auto-acquire them; the operator must layer the modifier on top deliberately.
Unlike the first four, `module-bytes-read` also gates two concrete tools on its
own because byte-export is the entire privileged action.

The taxonomy is enforced at startup by `ToolScopeRegistry.Build` (fails fast if any
`[McpServerTool]` is missing both `[RequireScope]` and `[RequireAnyScope]`) and at call
time by `ToolScopeAuthorizationFilter`.

### 2.15 Strawman reconciliation

The strawman in issue #166 listed nine scope buckets; this RFC lands ten primary scopes
plus five modifier scopes. Differences:

- **Added `sensitive-heap-read`** (modifier) — needed to subsume B4's
  `AllowSensitiveHeapValues` without conflating "topology" and "contents".
- **Added `eventsource-any`** (modifier) — needed to subsume the
  `collect_event_source` `unsafeProvider=true` gate.
- **Added `symbols-remote`** (modifier) — needed to subsume B4's symbol-path SSRF
  allowlist for the dotnet-symbols redirection branch.
- **Added `orchestrator-admin`** (modifier) — needed to subsume B3's
  `AllowCrossSessionAdmin`.
- **Added `module-bytes-read`** (literal modifier) — needed to gate the cross-MCP
  byte-export tools added for orchestrator mode without letting `root` / `*`
  silently export pod-local binaries and dumps.
- **`job-control` reserved but unused** — strawman split the legacy
  `get_collection_status` / `cancel_collection` MCP surfaces into a dedicated bucket.
  Those tools were retired in RFC 0002 Stage B (issue #211), so no production tool
  maps to this scope today. The name is held in
  reserve (§2.11) for a future watchdog-style role.
- **Moved `start_investigation` into `investigation-export`** (strawman had it loose).
- **Stacked `ptrace` on top of `heap-read` for `inspect_live_heap`** rather than minting
  a `heap-read-live` synonym.
- **`query_collection` uses `RequireAnyScope("read-counters", "eventpipe")`** as a
  coarse approximation of the §2.12 handle-ownership rule until the handle store grows
  per-handle `RequiredScopes` metadata. Tracked as a follow-up to B5.2 (this is the
  *only* divergence from §2.12 in the shipping implementation).
- **Modifier scopes use literal-membership checks** (`HasExplicitScope`) rather than
  honouring the `root`/`*` wildcard, so the legacy single-token `MCP_BEARER_TOKEN`
  bearer keeps working for every primary scope but does **not** auto-acquire any
  modifier — operators opt in deliberately.
- **Strawman tools that don't exist:** none. All eight strawman names map to real
  `[McpServerTool]` decorations.

## 3. Token shape

### 3.1 Option A — Multiple bearers, each with a scope set

Configuration lives under the canonical `Auth:BearerTokens` section (binder shape
matches the JSON in §6.1; ASP.NET Core env-binder maps `:` to `__`):

```bash
# Token 1: a dashboard, read-only.
Auth__BearerTokens__0__Name=dashboard
Auth__BearerTokens__0__Token=8f5...e1c
Auth__BearerTokens__0__Scopes__0=read-counters

# Token 2: an investigator, allowed to drive EventPipe but not dump.
Auth__BearerTokens__1__Name=oncall-investigator
Auth__BearerTokens__1__Token=2c9...44a
Auth__BearerTokens__1__Scopes__0=read-counters
Auth__BearerTokens__1__Scopes__1=eventpipe
Auth__BearerTokens__1__Scopes__2=heap-read
Auth__BearerTokens__1__Scopes__3=investigation-export
Auth__BearerTokens__1__Scopes__4=job-control

# Token 3: platform operator.
Auth__BearerTokens__2__Name=platform-ops
Auth__BearerTokens__2__Token=...
Auth__BearerTokens__2__Scopes__0=*
```

The middleware:

1. Reads `Auth:BearerTokens` into a `BearerTokenRegistry` once at startup.
2. For each request, extracts the `Bearer <opaque>` header.
3. Uses `CryptographicOperations.FixedTimeEquals` against each registered token (cost is
   bounded; deployments are expected to have ≤10 tokens) and resolves the scope set.
4. Attaches the `(tokenName, scopes)` tuple to `HttpContext.Items`.
5. The MCP tool layer wraps each `[McpServerTool]` handler in a `[RequireScope("…")]`
   filter that asserts the requested scope is in the set; deny path returns the
   structured `Unauthorized` envelope already used elsewhere.

**Pros.**

- Zero new dependencies; uses the standard configuration binder.
- Operators already understand "rotate this secret" — same primitive, just N of them.
- No clock dependence — no JWT expiry, no NTP failure mode.
- Plays well with Helm's existing `valueFrom: secretKeyRef` pattern.

**Cons.**

- Tokens are opaque — no embedded expiry; rotation is operator-driven.
- No standard SSO/OIDC story.
- O(N) FixedTimeEquals per request; trivial up to ~50 tokens.

### 3.2 Option B — JWT with `scope` claim

The middleware validates RS256/ES256 signatures against a configured public key
(`MCP_JWT_SIGNING_KEY` / JWKS URL), reads `scope` and `aud` claims, and checks `exp`.

**Pros.**

- Centralised issuer; rotation is "rotate the key".
- Native OIDC integration (cluster Keycloak / Entra / Auth0).
- Built-in expiry.

**Cons.**

- New dependency: `Microsoft.IdentityModel.Tokens` + `Microsoft.AspNetCore.Authentication.JwtBearer`.
- Key rotation story (JWKS caching, kid handling) is non-trivial.
- More moving parts in an air-gapped or single-tenant sidecar deployment, which is the
  current dominant topology (`docs/local-docker-sidecar.md`).

### 3.3 Recommendation

**Option A for v1.** It matches the existing middleware shape
(`Auth/BearerTokenMiddleware.cs:65` already uses `FixedTimeEquals`), adds no
dependencies, and is achievable in the B5.1 sub-issue without coordinating with an
identity provider rollout.

We commit to keeping the abstraction (`IPrincipalResolver` returning
`(tokenName, ImmutableHashSet<string> scopes)`) JWT-friendly. The migration to Option B
becomes a single new `IPrincipalResolver` implementation plus configuration — no changes
to `[RequireScope]` consumers. The migration is filed as a non-blocking future issue
under the `Out of scope` section (§10).

## 4. Per-call confirmation for destructive primitives

The question: even after a scope check passes, should the most dangerous tools require
an explicit `confirm=true` parameter as defense in depth?

| Tool | Scope | Defense-in-depth `confirm=true`? | Rationale |
|---|---|---|---|
| `collect_process_dump` | `dump-write` + `ptrace` | **Yes** | Writes a multi-GB file containing the entire address space. The cost of an accidental call is unbounded. `confirm=true` makes "I meant to dump production" an explicit, audit-loggable utterance. |
| `capture_method_bytes` | `ptrace` | No | Read-only, bounded output (one method body). Friction would push callers to skip per-method drilldowns. |
| `inspect_live_heap` | `heap-read` + `ptrace` | No | Read-only walk; suspend window is sub-second on typical heaps. Already gated by two scopes — that *is* the defense in depth. |
| `collect_thread_snapshot` | `ptrace` | No | Read-only; minimal suspend. |

**Argument for confirm-everywhere.** Symmetry — every privileged scope behaves the
same. Easier to document ("everything `ptrace` needs `confirm=true`"). Marginal cost on
the read-only tools is a single bool parameter.

**Argument against confirm-everywhere.** The strongest argument for `confirm=true` is
*irreversibility* (you can't un-write a dump file) or *unbounded blast radius* (you
can't un-disclose 4 GB of heap). The `ptrace`-stack read tools have neither property —
the output is bounded by the MCP response cap, the suspend is brief, and the
operations are idempotent. Adding `confirm=true` to read tools trains callers to set it
reflexively, which destroys its value when it actually matters.

**Decision.** `confirm=true` on `collect_process_dump` only. Filed as sub-issue B5.6.
The parameter rejects with a structured `ConfirmationRequired` envelope when omitted,
and the rejection is logged at Info (it's a misuse signal, not an attack).

> **Status — B5.6 shipped.** `collect_process_dump` now carries a `confirm: bool = false`
> parameter (PR closing #187). Without `confirm=true` the tool returns a
> `{ kind: "confirmation_required", ... }` envelope describing the dump that *would*
> have been written (`targetPid`, `dumpType`, `outputDirectory`) and writes nothing to
> disk — no process attach, no `createdump` invocation. With `confirm=true` the
> behaviour is identical to before, still gated by the `dump-write` + `ptrace` scope
> stack from §2.6. The other ptrace-stack tools (`capture_method_bytes`,
> `inspect_live_heap`, `collect_thread_snapshot`) deliberately remain confirmation-free
> per the decision matrix above — adding `confirm=true` to read-only tools would
> condition callers to set it reflexively and destroy its signal.

## 5. Default policy by transport

The non-loopback bind guard already exists at `Program.cs:97-113`. This RFC widens it in
two steps so v1 lands without breaking existing deployments.

### 5.1 v1 behaviour (this RFC, first release)

| Transport | Default token resolution | Rationale |
|---|---|---|
| **stdio** (`--stdio`) | Synthetic in-memory token with `*` scope. | The MCP client *is* the process owner — same trust boundary as a local CLI. Preserves current behaviour. No bearer is ever transmitted on a network. |
| **Loopback HTTP** (`127.0.0.1` / `[::1]`) | Legacy `MCP_BEARER_TOKEN` → `*` scope, or any configured `Auth:BearerTokens` scopes if present. | Developer ergonomics. The transport is unreachable from outside the host. |
| **Non-loopback HTTP** | `Auth:BearerTokens` if present (each entry must declare a non-empty scope set). **Falls back to** the existing `Program.cs:97-113` guard: legacy `MCP_BEARER_TOKEN` is still accepted but logs a `Warning` per §7.1. | Existing networked deployments keep working; operators are nudged to migrate. |

### 5.2 v2 behaviour (one release later)

The non-loopback fallback to legacy `MCP_BEARER_TOKEN` is removed. The startup check
becomes:

> Refusing to start: server is configured to bind to a non-loopback address but no
> `Auth:BearerTokens` entries are configured. Per RFC 0001, networked deployments must
> declare at least one scoped bearer; the legacy `MCP_BEARER_TOKEN`-only path was
> deprecated in the previous release and is no longer accepted on non-loopback binds.
> See `docs/rfcs/0001-per-tool-authorization-scopes.md` §7.

stdio and loopback HTTP are unchanged in v2. The v1 → v2 transition is the only
breaking change introduced by this RFC and is signposted in both §5.2 and §7.1.

## 6. Wire format and config examples

### 6.1 `appsettings.json` (precedence — env vars still override per ASP.NET Core rules)

```json
{
  "Auth": {
    "BearerTokens": [
      {
        "Name": "dashboard",
        "Token": "8f5e0c1a...",
        "Scopes": ["read-counters"]
      },
      {
        "Name": "oncall-investigator",
        "Token": "2c9447aa...",
        "Scopes": [
          "read-counters",
          "eventpipe",
          "heap-read",
          "investigation-export",
          "job-control"
        ]
      },
      {
        "Name": "platform-ops",
        "Token": "...",
        "Scopes": ["*"]
      }
    ]
  }
}
```

(Env binder equivalent: `Auth__BearerTokens__0__Name=dashboard`, etc. — same shape as
§3.1.)

### 6.2 Helm `values.yaml`

```yaml
mcp:
  bearerTokens:
    - name: dashboard
      valueFrom:
        secretKeyRef:
          name: mcp-bearer-tokens
          key: dashboard
      scopes:
        - read-counters
    - name: oncall-investigator
      valueFrom:
        secretKeyRef:
          name: mcp-bearer-tokens
          key: oncall
      scopes:
        - read-counters
        - eventpipe
        - heap-read
        - investigation-export
        - job-control
    - name: platform-ops
      valueFrom:
        secretKeyRef:
          name: mcp-bearer-tokens
          key: platform-ops
      scopes: ["*"]
```

The chart renders each entry into the deployment as:

```yaml
env:
  - name: Auth__BearerTokens__0__Name
    value: dashboard
  - name: Auth__BearerTokens__0__Token
    valueFrom:
      secretKeyRef: { name: mcp-bearer-tokens, key: dashboard }
  - name: Auth__BearerTokens__0__Scopes__0
    value: read-counters
  # ...
```

### 6.3 Kubernetes Secret layout

**Recommended: single multi-key Secret.**

```yaml
apiVersion: v1
kind: Secret
metadata:
  name: mcp-bearer-tokens
type: Opaque
stringData:
  dashboard: 8f5e0c1a...
  oncall: 2c9447aa...
  platform-ops: ...
```

**Alternative: N single-key Secrets** (one per token). Use when different RBAC subjects
should be able to rotate different tokens — e.g. SRE rotates `platform-ops`, the
on-call rotation rotates `oncall`. Costlier in audit setup; only adopt if the RBAC
split is real.

Either way, the *names* (`dashboard`, `oncall`, `platform-ops`) are non-sensitive and
appear in logs (see §8); only the values are.

### 6.4 Cloud Run / Secret Manager (sketch)

The MCP server is also deployable as a Cloud Run sidecar (see
`docs/cloud-integrations-design.md`). The mapping is mechanically identical to §6.2:

- Each token value lives as a Secret Manager secret version.
- The Cloud Run service definition mounts each secret as an env var via the
  `--set-secrets` flag: `--set-secrets=Auth__BearerTokens__0__Token=mcp-dashboard:latest`,
  etc.
- Scope lists and `Name` are passed as plain `--set-env-vars`
  (`Auth__BearerTokens__0__Scopes__0=read-counters`).

Cloud Run-specific Helm-equivalent (KRM/Config Connector) wiring is tracked separately
and is **out of scope** for this RFC; the env shape above is what any deployer ships
against.

## 7. Backward compatibility

The compatibility contract has three levels.

### 7.1 Legacy `MCP_BEARER_TOKEN`

| Mode | Behaviour |
|---|---|
| Only `MCP_BEARER_TOKEN` set, stdio or loopback | Token resolves to a synthetic registry entry `{Name: "legacy", Scopes: ["*"]}`. Identical to current behaviour. No warning. |
| Only `MCP_BEARER_TOKEN` set, non-loopback | **v1 of this RFC:** still accepted (per §5.1), but a `Warning`-level log is emitted on every startup: *"`MCP_BEARER_TOKEN` is set without `Auth:BearerTokens`; the legacy variable resolves to root scope and is deprecated. See RFC 0001."* **v2 (one release later):** refused — see §5.2. |
| Both `MCP_BEARER_TOKEN` and `Auth:BearerTokens` set | The scoped registry wins. `MCP_BEARER_TOKEN` is ignored; a `Warning`-level log is emitted at startup naming the ignored variable. |

### 7.2 B3 `Orchestrator:AllowCrossSessionAdmin`

| Release | Behaviour |
|---|---|
| **v1 of this RFC** | Both mechanisms work. If the flag is `true`, an `orchestrator-admin` synthetic grant is added to every registered token (preserving current behaviour) and a `Warning` log fires: *"`Orchestrator:AllowCrossSessionAdmin=true` is deprecated; grant `orchestrator-admin` scope to specific tokens instead. See RFC 0001 §2.9."* |
| **v2** (one release later) | Flag removed. Cross-session admin only via `orchestrator-admin` scope. |

### 7.3 B4 `Diagnostics:AllowSensitiveHeapValues` (and the two B4 allowlists)

The three B4 gates have **different fates** in v2:

| Gate | v2 disposition |
|---|---|
| `Diagnostics:AllowSensitiveHeapValues` | **Removed.** This is the only B4 flag truly going away. Sensitive output is only available via the `sensitive-heap-read` modifier scope. |
| `Diagnostics:EventSourceAllowlist` | **Retained** as a content allowlist. Independent of `eventsource-any` (callers without the scope can still capture allowlisted providers). |
| `Diagnostics:SymbolServerAllowlist` | **Retained** as an SSRF allowlist. Independent of `symbols-remote` (callers without the scope can still use allowlisted hosts). |

What's deprecated is the **pattern of relying on a deployment-wide setting for
caller-level distinction**. Operators should mint scoped bearers when they need
to authorise one caller (the incident-response operator) above the baseline
without granting the privilege to every other consumer of the same MCP
endpoint.

| Release | Behaviour |
|---|---|
| **v1 of this RFC (B5.2)** | Scope path added. Predicate is `principal.HasExplicitScope(<scope>) OR <legacy-flag-or-allowlist-allows>` — either is sufficient. No deprecation telemetry yet. |
| **v1.x (B5.4 — this section)** | When the legacy flag / allowlist is the path that actually unlocked a call (the principal did NOT hold the matching modifier scope), the server emits a `Warning`-level log entry once per process per gate. The three messages are verbatim: <ul><li>`Diagnostics:AllowSensitiveHeapValues is deprecated. Grant the 'sensitive-heap-read' scope to the operator token instead. The flag will be removed in a future release.`</li><li>`Diagnostics:EventSourceAllowlist is bypassed by the 'eventsource-any' scope; configure scoped tokens instead of relying on the allowlist alone for caller-level distinction. The allowlist policy itself is retained.`</li><li>`Diagnostics:SymbolServerAllowlist is bypassed by the 'symbols-remote' scope; configure scoped tokens instead of relying on the allowlist alone for caller-level distinction. The allowlist policy itself is retained.`</li></ul> Modifier scopes are matched via `BearerPrincipal.HasExplicitScope` (literal — root/`*` does NOT auto-grant), so a privileged root token using an allowlisted resource will also trigger the corresponding warning. |
| **v2 (one release later)** | `Diagnostics:AllowSensitiveHeapValues` is removed. The two allowlist policies stay; the deprecation warnings on them are demoted to `Information` (or removed entirely) once the helm/manifest examples no longer mention the legacy pattern. |

## 8. Audit logging

Extends the H9/B1 token-log redaction (no bearer value is ever logged).

- **Authorized call.** `Information` level: `{ tokenName, scope, tool, sessionId }`.
- **Rejected call (token unrecognized).** `Warning` level: `{ tokenName: null,
  remoteIp, tool, missingScope: null }`. The 401 path; same shape as today.
- **Rejected call (scope missing).** `Warning` level: `{ tokenName, tool,
  missingScope, grantedScopes }`. The new 403-equivalent path.
- **Confirmation missing** (§4 path on `collect_process_dump`). `Information` level:
  `{ tokenName, tool: "collect_process_dump", reason: "ConfirmationRequired" }`. Not a
  security event — a misuse signal.

`tokenName` is the human-readable identifier from §6 (`dashboard`, `oncall-investigator`,
`platform-ops`) — never the token value. Bearer values continue to be redacted as
established by H9/B1.

## 9. Implementation breakdown (sub-issues to spawn on RFC acceptance)

| # | Title | Touches |
|---|---|---|
| B5.1 | `IPrincipalResolver + BearerTokenRegistry + scoped auth middleware + handle-ownership metadata` | New `Auth/IPrincipalResolver.cs` interface returning `(tokenName, ImmutableHashSet<string> scopes)`; `Auth/BearerTokenRegistry.cs` as the v1 implementation; rewrite of `Auth/BearerTokenMiddleware.cs`; new `Auth/RequireScopeAttribute.cs` + ASP.NET Core endpoint filter; extend `IDiagnosticHandleStore.Register` with a `RequiredScopes` field per §2.12 and enforce it on every `query_*` tool's effective scope check; back-compat shim for legacy `MCP_BEARER_TOKEN`; tests for scope resolution, `*` semantics, and handle-ownership enforcement. |
| B5.2 | `Apply [RequireScope] to every [McpServerTool]` | Mechanical: 32 attributes per §2. One PR, one diff, one reviewer pass. |
| B5.3 | `Subsume Orchestrator:AllowCrossSessionAdmin into orchestrator-admin scope` | `OrchestratorOptions.cs:140`, `OrchestratorTools.cs:384, 466`, `Hosting/InvestigationProxyEndpoints.cs:161`. Add deprecation log. |
| B5.4 | `Subsume Diagnostics:AllowSensitiveHeapValues into sensitive-heap-read scope` | `SecurityOptions.cs:32`, `SensitiveValueGate.cs:16`, `collect_event_source` deny path (`DiagnosticTools.cs:1316`). Add deprecation log. |
| B5.5 | `Helm chart scoped-token wiring (bearerTokens map)` | `deploy/k8s/` chart values + template, sample-sidecar manifest, `docs/local-docker-sidecar.md` updates. |
| B5.6 | `Per-call confirm=true on collect_process_dump (dump-write defense in depth)` | `DiagnosticTools.cs:1362`; new `ConfirmationRequired` envelope; tests. |

Each sub-issue references this RFC by path; B5.2 depends on B5.1; B5.3 / B5.4 / B5.6
can land in parallel after B5.2; B5.5 depends on B5.1's env shape being stable.

## 10. Out of scope (and why)

- **OIDC / SSO integration (Option B).** Deferred. The abstraction in §3.3 keeps the
  door open; no customer has asked for it yet.
- **Per-pod / per-namespace scoping.** Already covered by `Orchestrator:AllowedNamespaces`
  and the orchestrator's Kubernetes RBAC. Layering a scope-system namespace filter on
  top would duplicate (and inevitably drift from) what RBAC already enforces.
- **Changes to the MCP wire protocol.** Scope checks happen entirely inside the server's
  filter pipeline; the JSON-RPC envelope is unchanged. A future Option B migration may
  surface `aud`/`sub` claims to tools as an `HttpContext` item but still emits the same
  `Unauthorized` envelope on the wire.
- **Per-tool rate limits.** Orthogonal concern; tracked separately.
- **Encrypted-at-rest token store on the server.** Tokens are already in Kubernetes
  Secrets / Secret Manager; introducing an on-disk store would add an attack surface
  without changing the trust boundary.

---

*Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>*
