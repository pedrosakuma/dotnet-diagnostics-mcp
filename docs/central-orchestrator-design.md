# Central MCP orchestrator design
_Status: Phase 1 spike for [issue #20](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/issues/20)._
This document answers one question:
**How should `dotnet-diagnostics-mcp` expose a fleet of prepared Kubernetes Pods through one MCP endpoint without changing the current diagnostic tool bodies?**
Short answer:
**Add a central orchestrator that manages per-investigation ephemeral attaches and proxies the existing pod-local MCP server for exactly one target Pod at a time.**
This is intentionally complementary to the central-topology feasibility spike in PR #137, which introduced `docs/central-k8s-design.md` on its branch. That earlier document established that a central topology is viable only if the data plane still launches something close to the target Pod. This document focuses on the missing follow-up: the MCP **orchestrator** that turns the current single-pod recipe into a fleet-facing surface.
---
## 1. Context
### 1.1 What `CENTRAL-TOPOLOGY.md` ships today
`deploy/k8s/CENTRAL-TOPOLOGY.md` already documents an on-demand, operator-driven topology.
The flow today is:
1. A target Pod is prepared in advance with:
- a shared `/tmp` `emptyDir`, and
- a fixed UID/GID so the diagnostic socket is readable.
2. An operator patches `pods/ephemeralcontainers` to inject the `dotnet-diagnostics-mcp` image into that Pod.
3. The ephemeral container runs the existing MCP server in the target Pod's context.
4. The operator exposes it with `kubectl port-forward`.
5. The client then talks directly to that Pod-local server and uses the normal tool flow (`inspect_process(view="list")`, `collect_events(kind="counters")`, `collect_sample(kind="cpu")`, and so on).
That topology is already the correct answer for the runtime's hard constraints:
- the diagnostics process must see the target PID,
- it must see the same `/tmp/dotnet-diagnostic-*` socket path,
- it must run under the same UID,
- and some ClrMD-backed tools additionally need `CAP_SYS_PTRACE`.
`docs/central-k8s-design.md` therefore made the right earlier decision: central topology is feasible, but only if the attach primitive still runs **in or extremely near** the target Pod context. The recommended primitive remains a **per-investigation ephemeral debug container**.
### 1.2 What gap remains
The current topology is still operator-driven and single-pod.
A human or an external script must still:
- choose the Pod,
- apply the patch,
- wait for it to become Running,
- open the port-forward,
- and only then give the endpoint to the LLM.
That is acceptable for ad-hoc manual use, but it does **not** give the model a single "fleet of pods" MCP endpoint.
Issue #20 closes that gap.
The orchestrator should give the LLM one logical MCP endpoint that can:
- enumerate candidate Pods,
- attach to one prepared Pod on demand,
- keep a stable investigation handle for that target,
- and forward the **existing** diagnostic tools through that handle.
### 1.3 What the orchestrator is and is not
The orchestrator is **not** a new runtime diagnostics engine.
It does not replace the Pod-local MCP server. It adds a control plane in front of it.
- **Before attach:** the user sees a fleet-level surface.
- **After attach:** the user still sees the same existing diagnostic tools, but now scoped to one attached Pod.
That distinction matters. The orchestrator is a **session and routing layer**. It turns:
- one target Pod,
- one ephemeral-container attach,
- one proxied in-Pod MCP server,
into:
- one fleet-aware investigation handle,
- one stable client session,
- one consistent tool surface.
### 1.4 Problem statement
The problem issue #20 solves is therefore:
> move from **operator-selected single-pod attach** to **one MCP endpoint that
> exposes a fleet of prepared Pods as attachable investigation targets**.
That is the whole scope of this spike.
---
## 2. Goals and non-goals
### 2.1 Goals
- Define the orchestrator shape for a **single MCP endpoint** that fronts many prepared Pods.
- Keep the central topology **stateless**.
- Reuse the existing on-demand attach model from `deploy/k8s/CENTRAL-TOPOLOGY.md`.
- Keep the current diagnostic tool implementations unchanged as much as possible.
- Specify a minimal new fleet-aware tool surface.
- Define the proxying model after attach.
- Document the session lifecycle, auth boundary, RBAC, and threat model.
- Produce an implementation plan split into follow-up PRs.
- Keep this PR design-only: **no C# changes, no YAML changes, no integration tests**.
### 2.2 Non-goals
The issue's **Out of scope** list is copied verbatim below:
- Cross-cluster fan-out.
- Persistent storage of investigation results (the central topology stays stateless; the LLM persists summaries via `export_investigation_summary`).
- Unprepared-target attach (tracked separately — requires `/proc/<pid>/root/tmp` traversal in the diag server).
Additional non-goals for this spike:
- Auto-discovery of **unprepared** targets.
- Replacing the existing sidecar or operator-driven on-demand recipes.
- Changing every existing diagnostic tool in this PR.
- Inventing a second diagnostics protocol alongside MCP.
- Reconstructing or adopting orphaned sessions after orchestrator restart.
- Cross-cloud or cloud-provider-specific target discovery beyond the Kubernetes API.
### 2.3 Constraints carried forward
The orchestrator intentionally inherits the same constraints as the current on-demand topology:
- prepared targets only,
- attach lifetime is **investigation-scoped**, not request-scoped,
- no cross-cluster routing,
- no persistent server-side state,
- and no silent mutation of every Pod in the namespace.
### 2.4 Kubernetes-only scope; serverless container hosts use sidecar recipes
The orchestrator is intentionally **Kubernetes-only**. Its data plane depends on the Kubernetes API surface — pod listing, ephemeral container injection, `pods/portforward` — and on Linux primitives (UID match, shared PID namespace, `CAP_SYS_PTRACE`) that serverless container hosts either do not expose or expose differently per provider. There is no plan to grow a parallel orchestrator for AWS ECS / Fargate, GCP Cloud Run, Azure Container Apps, or Azure App Service.
For those serverless container hosts the answer is the **per-service sidecar recipes** under `deploy/azure/`, `deploy/aws/`, and `deploy/gcp/`. Each recipe wires one diagnostics MCP sidecar next to one target container in the same task / revision, sharing `/tmp` for the diagnostic IPC socket, and the MCP client talks to that endpoint directly. That is a smaller surface than the orchestrator (no fleet enumeration, no attach lifecycle, no portforward proxy) but it is the right shape for hosts where "attach a debug container to an existing workload" is not a first-class primitive. The cross-host capability matrix is documented in [`docs/cloud-recipes-design.md`](./cloud-recipes-design.md).
---
## 3. Tool surface
### 3.1 Principle
The orchestrator should add exactly four new MCP tools, matching issue #20:
- `list_orchestrator(kind="pods")`
- `attach_to_pod`
- `detach`
- `list_orchestrator(kind="investigations")`
Everything else should remain the existing diagnostic tool surface, reached through an attached investigation context.
That keeps the new surface concept-oriented:
- discovery,
- attach,
- detach,
- session introspection.
### 3.2 Envelope and error conventions
The current server uses `DiagnosticResult<T>` envelopes:
- `summary`
- `hints`
- `data`
- optional `error`
- optional `handle`
- optional `handleExpiresAt`
The new orchestrator tools should follow the same pattern.
A success therefore returns `DiagnosticResult<TData>` with `data`; a failure returns `DiagnosticResult<TData>` with `error` and recovery hints.
The generic error shape should remain: a short `summary`, one or more recovery `hints`, and `error = { kind, message, detail }`. For example, `PodNotPrepared` should explain both the missing prerequisite and the next suggested tool call. The exact wire casing can follow the current serializer; the important design choice is the stable `error.kind` and the existing discoverability-aware response envelope.
### 3.3 Shared payload types
The four tools only need four small orchestrator-specific data shapes:
- `PodCandidate`: namespace, pod/container identity, phase/readiness, created time, owner/image, labels, a `diagnosticsPrepared` bool, a preparation reason, and `activeInvestigationCount`.
- `AttachSession`: handle, target identity, ephemeral container name, state (`attaching|active|closed|expired|failed`), attach/last-used/expiry timestamps, proxied scope, and warnings.
- `ActiveInvestigation`: the `AttachSession` summary plus `idleSeconds`, optional client tag, and whether the port-forward/proxy is still active.
- `DetachResult`: handle, previous state, detach timestamp, `podStillContainsEphemeralContainer`, the resources that were closed, and warnings.
### 3.4 `list_orchestrator(kind="pods")`
#### Proposed signature
```text
list_orchestrator(kind="pods")(
  namespace?: string,
  labelSelector?: string,
  fieldSelector?: string,
  containerName?: string,
  preparedOnly?: bool = true,
  includeNotReady?: bool = false,
  limit?: int = 100,
  cursor?: string? = null
)
```
#### Return shape
`list_orchestrator(kind="pods")` should return a paged list of `PodCandidate` rows inside the normal `DiagnosticResult<T>` envelope. The `data` payload should be `{ items: PodCandidate[], nextCursor?: string }`, with enough metadata for the LLM to choose a single Pod without another lookup.
#### Error kinds
- `InvalidArgument`
- `NamespaceNotAllowed`
- `SelectorRejected`
- `TooManyResults`
- `PermissionDenied`
- `KubeApiUnavailable`
`SelectorRejected` is important: the orchestrator should not allow arbitrary selectors that escape its configured allowlist or namespace boundary.
### 3.5 `attach_to_pod`
#### Proposed signature
```text
attach_to_pod(
  namespace: string,
  pod: string,
  container?: string = "app",
  ttlSeconds?: int = 1800,
  requirePreparedTarget?: bool = true,
  allowReuseExistingSession?: bool = true
)
```
#### Return shape
`attach_to_pod` returns an `AttachSession` plus the normal `handle` and `handleExpiresAt` fields on the outer envelope. The `data` payload should include the target identity, the ephemeral container name, the state, the expiry, and any warnings. The summary should explicitly tell the user that the returned handle is active and that the **existing** diagnostic tools are now scoped to the attached Pod.
#### Error kinds
- `InvalidArgument`
- `NamespaceNotAllowed`
- `PodNotFound`
- `ContainerNotFound`
- `PodNotRunning`
- `PodNotPrepared`
- `AttachAlreadyInProgress`
- `AttachFailed`
- `AttachTimeout`
- `PermissionDenied`
- `PortForwardFailed`
- `KubeApiUnavailable`
`AttachFailed` should mean the patch was accepted but the attach never became usable. `AttachTimeout` should mean the Pod may still recover and a retry is reasonable.
### 3.6 `detach`
#### Proposed signature
```text
detach(handle: string)
```
#### Return shape
`detach` returns `DetachResult` and must say clearly that the target Pod still contains the ephemeral diagnostics container until the Pod is recreated. The `data` payload should include the prior state, the detach timestamp, the resources that were closed, and a warning that Pod restart or recreation is the real cleanup step.
#### Error kinds
- `InvalidArgument`
- `SessionNotFound`
- `SessionAlreadyClosed`
- `PermissionDenied`
`detach` should be safe to retry, but it should still communicate whether the handle was already closed.
### 3.7 `list_orchestrator(kind="investigations")`
#### Proposed signature
```text
list_orchestrator(kind="investigations")(
  namespace?: string,
  pod?: string,
  includeClosed?: bool = false,
  limit?: int = 100,
  cursor?: string? = null
)
```
#### Return shape
`list_orchestrator(kind="investigations")` returns a paged list of `ActiveInvestigation` entries. The `data` payload should be `{ items: ActiveInvestigation[], nextCursor?: string }`, with enough lease and activity metadata to decide whether a handle should be reused, detached, or allowed to expire.
#### Error kinds
- `InvalidArgument`
- `NamespaceNotAllowed`
- `PermissionDenied`
### 3.8 How existing tools target the attached Pod
Phase 1 should **not** redesign every existing tool signature.
The recommended model is:
- the four new tools manage fleet selection and investigation handles,
- all existing diagnostic tools keep their current names and payloads,
- the orchestrator resolves the target Pod from **session context**, not by forcing a new `targetHandle` parameter onto every tool on day one.
Two models are possible:
1. **MCP session binding**
- after `attach_to_pod`, the current client session becomes bound to one investigation handle until `detach` or another `attach_to_pod` call.
2. **Explicit `investigationHandle` argument**
- existing tools gain an optional `investigationHandle` parameter.
Recommendation for Phase 1:
- prefer **MCP session binding** as the default behavior,
- allow an explicit `investigationHandle` later only if multi-target concurrency in one client session becomes a hard requirement.
That keeps the existing tool signatures stable while the orchestrator remains an implementation detail in front of them.
### 3.9 Tool count budget
`AGENTS.md` warns that Anthropic recommends roughly **10 tools** per LLM context and notes that the repo had already grown to **20** because each added concept unlocked distinct diagnostic behavior.
Issue #20 proposes four more tools, which takes the surface to the moral -equivalent of **24** under that same budget framing.
That needs justification.
#### Why not collapse into one `manage_fleet(view=...)` tool?
Because these concepts are distinct in ways that matter to both the user and the implementation:
- `list_orchestrator(kind="pods")` is **read-only fleet discovery**.
- `attach_to_pod` is a **privileged side effect** that mutates a target Pod and allocates orchestrator resources.
- `detach` is **cleanup** with intentionally different semantics from attach.
- `list_orchestrator(kind="investigations")` is **session introspection**, not pod discovery.
These are not just four views over the same artifact. They map to different RBAC-sensitive operations and different user intents.
#### Why the four-tool split is acceptable
Each addition earns its place:
1. `list_orchestrator(kind="pods")`
- needed before any attach can happen.
2. `attach_to_pod`
- the only action that actually creates a scoped investigation.
3. `detach`
- required because ephemeral attaches have orchestrator-local cleanup even when Kubernetes cleanup is impossible.
4. `list_orchestrator(kind="investigations")`
- required to explain and manage the orchestrator's otherwise invisible session state.
This is therefore a justified case of adding tools because the concepts are truly distinct, side-effect boundaries are different, and compressing them would harm clarity more than it would help the budget.
---
## 4. Proxy mechanics
After `attach_to_pod` succeeds, the orchestrator must route all subsequent tool calls for that investigation to the Pod-local MCP server.
Three options are viable.
### 4.1 Option A — HTTP reverse proxy over `kubectl port-forward`
#### Shape
- Orchestrator shells out to `kubectl port-forward pod/<name> 8787:8787`.
- Orchestrator then speaks HTTP to `localhost:<ephemeral-port>/mcp`.
- The Pod-local server remains the source of truth for the existing tools.
#### Pros
- Fastest prototype.
- Easy to debug locally because the same commands already exist in the docs.
- Low protocol risk because the data plane stays normal streamable HTTP MCP.
#### Cons
- Hard dependency on the `kubectl` binary.
- Process lifecycle management is brittle under load.
- Harder stdout/stderr handling and retry classification.
- Harder credential story in-cluster.
- Makes the orchestrator feel like a wrapper script instead of a server.
#### Verdict
Good for a throwaway spike, not for the first real implementation.
### 4.2 Option B — in-process Kubernetes client plus direct port-forward streams
#### Shape
- Orchestrator uses the Kubernetes API directly.
- It patches `pods/ephemeralcontainers` itself.
- It waits for the ephemeral container to be Running.
- It opens a port-forward stream through the kube API in-process.
- It runs a small reverse proxy or MCP-aware forwarding layer per investigation handle.
#### Pros
- No external `kubectl` dependency.
- Single auth model for all cluster operations.
- Better classification of API, readiness, and transport failures.
- Easier connection pooling, backoff, TTL enforcement, and audit logging.
- Easier to package as a real service in-cluster.
- Lets the orchestrator generate a **per-attach Pod-local bearer token** and inject it into the ephemeral container env without exposing that token to the external client.
#### Cons
- More implementation work up front.
- Requires careful handling of long-lived streams.
- Still must deal with Pod readiness races and reconnects.
#### Verdict
**Recommended.**
This is the cleanest boundary for the real feature.
The orchestrator stays an HTTP MCP server to clients, uses the kube API natively, and proxies the existing Pod-local MCP endpoint over a controlled in-process transport.
### 4.3 Option C — MCP-over-MCP delegation
#### Shape
- The orchestrator becomes a meta-MCP client.
- It opens an MCP session to the Pod-local server.
- It forwards `tools/list` and `tools/call` semantics at the protocol layer instead of acting like an HTTP reverse proxy.
#### Pros
- Conceptually elegant.
- Natural fit if we later want to fan out to multiple backends or compose MCP servers.
- Could allow per-tool mediation, annotation, or audit at the protocol level.
#### Cons
- More protocol machinery than needed for Phase 1.
- Still requires the same underlying port-forward or equivalent data-plane transport.
- Risks weirdness around session lifecycle, notifications, and streaming.
- Higher surface area for spec drift and client quirks.
#### Verdict
Promising long-term, unnecessary for the first implementation.
### 4.4 Recommendation
Recommend **Option B: in-process kube client plus direct port-forward streams**.
Reasoning:
- it preserves the existing Pod-local MCP server unchanged,
- avoids taking a runtime dependency on `kubectl`,
- keeps auth and retries inside one service boundary,
- makes attach failures easier to classify,
- and leaves room for future MCP-over-MCP composition if that ever proves useful.
In short:
> use Kubernetes-native orchestration and transport, not shell-driven proxying,
> and keep the orchestrator protocol-visible only at the outer client boundary.

### 4.5 Cross-MCP handoff in orchestrator mode
The diagnostics MCP routinely emits **filesystem-bound handoff payloads** consumed by sibling MCPs:
- `MethodIdentity { mvid, metadataToken }` → `dotnet-assembly-mcp.get_method` needs the matching `.dll`/`.pdb` reachable on the **assembly-mcp host's filesystem**.
- `NativeFrame { buildId, imagePath }` → `dotnet-native-mcp.load_native_binary` needs the binary at that path.
- `inspect_heap(source="dump")(dumpFilePath)` is a raw local path.

In single-target sidecar mode (the topology shipped today) all three MCPs run side-by-side on the same host and the handoff "just works". In orchestrator mode the picture changes:

```
LLM client host                 │ Kubernetes cluster
  dotnet-assembly-mcp  ◄────────│ ... NO direct path to pod filesystem
  dotnet-native-mcp    ◄────────│
  dotnet-diagnostics-mcp ──HTTP──► orchestrator ──port-forward──► pod-local diagnostics MCP
                                                                    │
                                                                    └─ /app/MyApp.dll lives only here
```

When the LLM passes a `MethodIdentity` from a pod-remote CPU sample to a client-side `dotnet-assembly-mcp`, the MVID is correct but the on-disk bytes are absent → `path_not_found`.

**What still works** in orchestrator mode (these stay inside one server boundary):
- All `*_handle`-based drilldowns (`query_snapshot(view="call-tree")`, `query_snapshot`, `query_snapshot`, `query_snapshot`, `query_snapshot`) — the handle store lives in the pod-local server, so subsequent calls forwarded over the proxy hit the same store.
- Tools that don't export filesystem-bound identities (`collect_events(kind="counters")`, `inspect_process(view="container")`, `inspect_process(view="memory_trend")`, `inspect_process(view="info")`, `start_investigation`, `export_investigation_summary`, `compare_to_baseline`).

**What breaks** without further work:
- Any `dotnet-assembly-mcp` / `dotnet-native-mcp` call whose argument originated as a handoff from the remote pod.
- `inspect_heap(source="dump")(dumpFilePath)` against a pod-local dump path.

**Mitigation options:**
1. **Co-located twin sidecars (operator-side workaround)** — deploy `dotnet-assembly-mcp` and `dotnet-native-mcp` as additional ephemeral containers alongside the diagnostics container. The orchestrator's `attach_to_pod` can mint per-attach bearer tokens for each, and the LLM client connects to the cluster trio via the orchestrator proxy. Pure configuration; no protocol change. Recommended default.
2. **Module-byte fetch endpoint (available)** — `get_bytes(kind="module")(moduleVersionId, asset, offset, maxBytes, processId?)` and `get_bytes(kind="dump")(dumpFilePath, offset, maxBytes)` now stream PE/PDB/dump bytes through normal MCP CallTool round-trips. The client materializes the chunks to a scratch dir, verifies the full-artifact SHA-256, then passes that local path to the client-side sibling MCPs. This adds surface and bandwidth, but it unblocks cross-MCP handoffs when twin sidecars are not feasible.
3. **MCP asset-URI convention** — promote handoffs from filesystem paths to a `mcp+pod://ns/pod/path` URI that participating MCPs know how to dereference via a callback into the diagnostics server. Cleanest end-state; requires alignment with the sibling MCPs and is **not a P7 item** — tracked as a Phase 9+ idea.

In practice the deployment guidance is now: prefer option (1) when you can co-locate the sibling MCPs; fall back to option (2) when the client-side sibling MCPs must stay off-cluster.

> **Security note — path hints are untrusted.** Every filesystem-bound payload
> in the table above (`ModulePath`, `imagePath`, `dumpFilePath`, the
> mitigation-2 `get_bytes(kind="module")` / `get_bytes(kind="dump")` sink path, the
> mitigation-3 `mcp+pod://` dereference target) is an *untrusted hint* that
> flows through the LLM. Consumer MCPs MUST canonicalise, allowlist a fixed
> set of roots, reject symlink escapes, and verify MVID / build-id before
> opening anything — see the new
> [Path hints are untrusted](./handoff-contract.md#path-hints-are-untrusted)
> section of the handoff contract for the producer/consumer rules and a
> worked example. This applies especially to the shipped **cross-MCP byte
> fetch** mitigation (option 2 above, [#144](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/issues/144)):
> the materialised scratch path the LLM hands to the client-side sibling MCP
> inherits the same untrusted-hint contract and must be validated identically.

---
## 5. Session lifecycle
### 5.1 Handle issuance
`attach_to_pod` should mint one opaque investigation handle per successful attach, for example:
```text
inv_<ulid>
```
The handle is owned by the orchestrator and maps in memory to:
- namespace,
- pod name,
- container name,
- ephemeral container name,
- per-attach Pod-local bearer token,
- active port-forward or stream state,
- session status,
- TTL metadata,
- last-used timestamp,
- and minimal audit fields.
### 5.2 Recommended TTL policy
Phase 1 should keep TTL simple and explicit:
- default idle TTL: **30 minutes**,
- maximum wall-clock TTL: **8 hours**,
- last-used refresh: yes, on successful proxied tool call,
- minimum allowed TTL request: **5 minutes**,
- maximum allowed TTL request from caller: clamp to policy.
That is long enough for a real investigation, short enough to avoid forgotten sessions, and prevents one attach from silently becoming a shadow sidecar.
### 5.3 State machine
```text
attaching -> active -> closed
          -> failed
active    -> expired
active    -> closed
```
#### `attaching`
Patch accepted; readiness wait in progress; no proxied tool calls allowed yet.
#### `active`
Pod-local MCP endpoint reachable; proxy healthy; existing diagnostic tools may be invoked through the orchestrator.
#### `closed`
User called `detach`, or the orchestrator closed the session deliberately.
#### `expired`
TTL elapsed and the orchestrator has closed transport resources.
#### `failed`
Attach never became usable, or transport could not be established.
### 5.4 What `detach` actually does
This needs to be explicit because the Kubernetes behavior is non-obvious.
`detach(handle)` does **not** remove the ephemeral container.
It only:
- marks the investigation closed,
- stops forwarding new MCP calls for that handle,
- tears down the port-forward or equivalent proxy stream,
- discards the Pod-local bearer token from orchestrator memory,
- and records that the session ended.
The target Pod will still contain the ephemeral diagnostics container until the Pod is restarted or recreated.
### 5.5 Reuse policy
Phase 1 should prefer **reusing an active handle** for the same `(namespace, pod, container)` when all of the following are true:
- the existing session is still `active`,
- the caller allows reuse,
- the current client is allowed to see that handle,
- and the proxy is healthy.
This avoids injecting multiple diagnostic ephemeral containers into one Pod when one already exists.
### 5.6 Reaper behavior
A background reaper should run inside the orchestrator at a small interval, for example every 60 seconds.
For each session:
- if state is `active` and idle TTL has elapsed, close it and mark `expired`;
- if the target Pod no longer exists, mark `closed` with warning `PodGone`;
- if the proxy dies permanently, mark `closed` or `failed` depending on whether the session had ever become active.
The reaper does **not** try to clean the target Pod's ephemeral container. That is impossible without deleting or recreating the Pod.
### 5.7 Orchestrator restart behavior
This design stays stateless.
That means orchestrator restart semantics must be intentionally simple:
- all in-memory session handles are lost,
- all existing external handles become invalid,
- all existing port-forward streams are gone by process exit,
- old ephemeral containers remain in their Pods,
- and clients must re-run `attach_to_pod` after restart.
Phase 1 should **not** attempt session adoption after restart.
### 5.8 Startup sweeper after restart
The orchestrator should still do one low-cost thing after restart:
- scan configured namespaces for ephemeral containers whose name matches the orchestrator prefix,
- log them as **stale investigation remnants**,
- surface them via audit logs or metrics,
- but do not try to adopt them into new handles.
That is enough to make restart fallout visible without violating statelessness.
---
## 6. RBAC + threat model
### 6.1 Required Kubernetes permissions
The issue states the orchestrator's ServiceAccount needs the following RBAC:
- `pods/ephemeralcontainers` (update/patch)
- `pods` (get/list/watch)
- `pods/portforward` (get/create)
Those are the right minimum verbs for the architecture proposed here.
### 6.2 Security posture
The repository already treats diagnostics access as highly privileged. For the orchestrator, that warning becomes stronger:
> the orchestrator's MCP bearer token is equivalent to shell-like diagnostic
> access on any Pod inside the orchestrator's scope.
Why that statement is correct:
- `attach_to_pod` can inject a diagnostics container,
- that container can inspect managed processes,
- existing tools can sample CPU, inspect heap, read strings, capture dumps, and otherwise observe sensitive runtime state,
- and some configurations will also include `CAP_SYS_PTRACE` or `CAP_PERFMON`.
This is not ordinary read-only telemetry. It is controlled, high-value debug access.
### 6.3 Primary threats
#### Threat 1 — token theft
If the outer MCP bearer token leaks, an attacker can enumerate candidate Pods, attach to allowed targets, and exfiltrate runtime internals through diagnostic tools.
#### Threat 2 — overly broad scope
If the orchestrator is deployed with a broad ClusterRole by default, one leaked credential may become a cross-tenant incident.
#### Threat 3 — selector abuse
If `list_orchestrator(kind="pods")` accepts arbitrary selectors, a user can enumerate workloads well outside the intended diagnostics set.
#### Threat 4 — stale or hidden attaches
Because ephemeral containers cannot be removed, investigations can leave a longer-lived diagnostic surface behind if operators do not recreate the Pod.
#### Threat 5 — audit gaps
Without logs that tie user, target, and tool call together, the orchestrator becomes a privileged black box.
### 6.4 Required mitigations
#### Prefer namespace-scoped RBAC when possible
The least-privilege deployment is still:
- one namespace,
- one `Role`,
- one `RoleBinding`,
- one `ServiceAccount`.

That said, issue #20 explicitly calls for a fleet-facing orchestrator that may span multiple namespaces, so the P5 deployment assets also ship a ClusterRole/ClusterRoleBinding example for that case. Treat cluster scope as an explicit escalation, and down-scope to a namespace-local Role/RoleBinding whenever a tenant only needs one namespace.
#### Label-selector allowlist
The orchestrator should enforce an allowlist such as:
```text
diagnosticsmcp/enabled=true
```
That means:
- `list_orchestrator(kind="pods")` only returns Pods matching the configured allowlist unless an operator explicitly widens policy,
- `attach_to_pod` rejects Pods outside that allowlist even when addressed by exact name.
This is the most important mitigation after namespace scoping.
#### Audit logging
Every fleet-sensitive action should be logged with at least:
- client identity or auth principal,
- namespace,
- pod,
- container,
- action name (`list_orchestrator(kind="pods")`, `attach_to_pod`, `detach`, proxied tool call),
- start time,
- end time,
- outcome,
- and handle id if applicable.
Heavy collectors should also log enough metadata to answer who captured a dump, on which Pod, and when.
### 6.5 Optional defense-in-depth follow-ups
These are good ideas, but not required to approve the design:
- separate EventPipe-only and ptrace-capable deployment profiles,
- rate limiting for dump and heap-inspection tools,
- per-session caps for artifact-producing tools,
- namespace denylist for system namespaces,
- and approval gates for high-impact collectors.
---
## 7. Authentication boundary
### 7.1 Two independent auth boundaries exist
The orchestrator has **two** auth problems, not one.
#### Boundary A — client to orchestrator
The external client authenticates to the orchestrator using the orchestrator's own bearer token.
#### Boundary B — orchestrator to Kubernetes API
The orchestrator authenticates to the cluster in order to:
- list Pods,
- patch `pods/ephemeralcontainers`,
- and open `pods/portforward` streams.
The two boundaries must stay conceptually separate.
### 7.2 Recommended client-to-orchestrator auth
For the outer MCP endpoint:
- keep bearer-token auth,
- but do **not** rely on generated ephemeral tokens in shared or production orchestrator deployments,
- because issue #20 explicitly elevates the bearer to shell-equivalent power.
Recommendation:
- local dev may still use `MCP_BEARER_TOKEN=devtoken`,
- real deployments should source the token from a Kubernetes Secret or external secret manager,
- and operators should rotate it on a regular cadence.
### 7.3 Recommended orchestrator-to-cluster auth
#### In-cluster deployment
Use the orchestrator's own ServiceAccount token.
That is the clean default because RBAC is local and reviewable, no kubeconfig distribution is required, and projected ServiceAccount tokens already support rotation.
#### Out-of-cluster deployment
Allow kubeconfig or exec-credential plugins for development and admin use. That is useful for local testing but should not be the primary operational story.
### 7.4 Per-attach Pod-local auth
There is also a third, smaller auth boundary inside the design: the orchestrator must authenticate to the Pod-local MCP server it just injected.
Recommendation:
- generate a fresh random `MCP_BEARER_TOKEN` per attach,
- inject it into the ephemeral container env,
- keep it only in orchestrator memory,
- and never expose it back to the external client.
That gives each attach its own internal credential and avoids reusing the outer orchestrator bearer on the data plane.
### 7.5 Token rotation implications
#### Outer bearer rotation
When the outer orchestrator token rotates, new client requests must use the new token. Existing sessions may be allowed to finish or may be cut over according to the server's general auth policy, but the design should not require restarting every target attach.
#### Kubernetes credential rotation
When projected ServiceAccount tokens rotate, the orchestrator should pick up the new token without restart and new kube API calls should use fresh credentials automatically through the client library.
#### Pod-local per-attach token rotation
Do **not** rotate Pod-local bearer tokens mid-session in Phase 1. Each attach gets one token for its lifetime. If the session expires, create a new attach with a new token.
### 7.6 Deployment assets
P5 ships the first production deployment surface under:
- [`deploy/k8s/orchestrator/`](../deploy/k8s/orchestrator) for raw manifests + Kustomize overlays,
- [`deploy/helm/dotnet-diagnostics-orchestrator/`](../deploy/helm/dotnet-diagnostics-orchestrator) for Helm,
- [`deploy/k8s/README.md`](../deploy/k8s/README.md) for operator quick starts and the runbook.

Those assets intentionally keep the orchestrator itself non-root and ptrace-free while the per-investigation ephemeral diagnostics container is expected to retain the pod-local UID / `/tmp` / capability contract described in §4 and [`deploy/k8s/CENTRAL-TOPOLOGY.md`](../deploy/k8s/CENTRAL-TOPOLOGY.md). The current P5 deployment docs call out that the attach implementation still needs a code follow-up to inject the shared `/tmp` volume mount automatically on Linux targets.
---
## Operational observability

Wave 1 of Phase 8 adds a first-class operations surface for the central orchestrator:

- **Prometheus metrics** at `/metrics` for attach success/failure, attach latency, active investigations, proxied tool calls, and TTL reaper evictions. The endpoint is scope-protected by default (`metrics-read`) and only becomes unauthenticated when `MCP_METRICS_OPEN=true` is set explicitly.
- **Structured audit events** on stdout for `audit.orchestrator.attach`, `audit.orchestrator.detach`, and `audit.orchestrator.proxy_call`. These records log principal name, target identity, handle id, outcome, and latency where relevant, but never tool arguments or bearer values.
- **Opt-in OpenTelemetry export** by wiring the orchestrator meter/activity source into OTLP only when `OTEL_EXPORTER_OTLP_ENDPOINT` is configured. Without that environment variable the server still serves local Prometheus scraping, but it does not push telemetry anywhere.

Operationally this closes three gaps that the original design intentionally deferred: SREs can alert on attach failures and latency regressions, auditors can answer "who attached to which pod when", and platform teams can scrape orchestrator health without parsing free-form logs.
---
## 8. Phased implementation plan
This feature should land as a sequence of small, reviewable PRs.
### P2 — server-side multi-target abstraction
**Status: shipped in PR #142.**

#### Goal
Introduce a target-selection and session-binding abstraction without yet adding Kubernetes attach behavior.
The purpose of P2 is to make the server capable of resolving:
- local process context,
- sidecar/local-dev context,
- or orchestrator investigation context,
without rewriting every tool body.
#### Dependencies
- This design doc.
- Alignment on whether target selection is session-bound by default.
#### What actually shipped
- New `ISessionTargetBindingStore` + `MemorySessionTargetBindingStore` (TTL-aware, thread-safe, lazy eviction on read) in `src/DotnetDiagnosticsMcp.Core/ProcessDiscovery/`.
- New `SessionTargetBinding` record with `(ProcessId, Source, ExpiresAt?)`.
- New `IProcessContextResolver.ResolveAsync(string? sessionId, int? requestedProcessId, CancellationToken)` overload. Default interface method delegates to the legacy overload so every external resolver implementation stays compatible.
- `ProcessContextResolver` accepts an optional `ISessionTargetBindingStore` via ctor. Resolution precedence: explicit pid > session binding > local auto-resolve > ambiguous/not-found error.
- New `ProcessContext.BindingSource` field tags the resolved target as `"explicit"`, `"session-binding:<source>"` or `"local-auto"`. Purely informational; existing `AutoResolved` flag unchanged.
- Binding store registered as singleton in `DiagnosticServiceRegistration.AddDiagnosticCoreServices`.
- Tool method signatures unchanged — P3 will plumb `McpServer.SessionId` through `ResolveContextAsync` when `attach_to_pod` lands, at which point every existing tool transparently becomes session-aware.
#### Test strategy
- 11 unit tests for the in-memory store (set/get/remove, TTL eviction, null/empty session id, per-session isolation, overwrite semantics) — `MemorySessionTargetBindingStoreTests`.
- 6 unit tests for the session-aware resolver overload (no-store fall-through, binding overrides local auto-resolve including ambiguous case, explicit pid beats binding, null sessionId equals legacy overload, default interface method covers legacy-only implementors) — `SessionAwareProcessContextResolverTests`.
- Full pre-existing suite (263 Core + 90 Integration) passes unmodified — zero behaviour regression.
### P3 — `list_orchestrator(kind="pods")` + `attach_to_pod` + proxy plumbing
#### Goal
Add the fleet-discovery and attach path:
- `list_orchestrator(kind="pods")`
- `attach_to_pod`
- kube API patching
- ephemeral-container readiness wait
- in-process port-forward proxying

#### Status
- **P3a — `list_orchestrator(kind="pods")` + Kubernetes client scaffolding: shipped.** New `OrchestratorOptions` config section (enabled flag, namespace allowlist, label-key allowlist, prepared-label key, RequirePreparedLabel toggle, MaxListLimit), `IKubernetesClientFactory` (in-cluster ServiceAccount projection with kubeconfig fallback), narrow `IKubernetesPodsApi` abstraction (so tests can stub without mocking the full k8s surface), `IPodInventory` + `KubernetesPodInventory` (namespace allowlist enforcement, label-selector key validation, preparedness verdict — label opt-in by default, heuristic when `RequirePreparedLabel=false`), and the `list_orchestrator(kind="pods")` MCP tool. Registered only when `Orchestrator:Enabled=true`; off by default so existing single-target deployments are unaffected. 17 unit tests cover policy + transport adaptation.
- **P3b — `attach_to_pod` + ephemeral-container injection + in-process port-forward proxy + session-binding plumbing: pending.**
#### Dependencies
- P2 target-context abstraction.
- Decision to use in-process kube client plus direct port-forward streams.
- Agreement on namespace-first scope and label allowlist policy.
#### Expected file touch list
- `src/DotnetDiagnosticsMcp.Server/Tools/DiagnosticTools.cs`
- new orchestrator/Kubernetes integration services under `src/DotnetDiagnosticsMcp.Server/`
- auth/config wiring in `src/DotnetDiagnosticsMcp.Server/Program.cs`
- potential new option models in `src/DotnetDiagnosticsMcp.Core/` if shared envelope types belong there
- `docs/tool-reference.md`
- `docs/client-setup.md`
- `README.md` summary paragraph if the server now supports orchestrator mode
#### Test strategy
- unit tests for selector validation and allowlist enforcement,
- unit tests for patch payload generation,
- unit tests for attach wait state transitions,
- mocked Kubernetes client tests for attach success/failure paths,
- a narrow proxy test that proves one proxied request reaches a fake Pod-local MCP endpoint.
### P4 — `detach` + `list_orchestrator(kind="investigations")` + reaper
#### Goal
Add the session-management half of the orchestrator:
- `detach`
- `list_orchestrator(kind="investigations")`
- idle TTL tracking
- reaper
- startup stale-session visibility
#### Dependencies
- P3 attach/session plumbing.
- Agreement on default TTL policy.
#### Expected file touch list
- `src/DotnetDiagnosticsMcp.Server/Tools/DiagnosticTools.cs`
- session store / lease services under `src/DotnetDiagnosticsMcp.Server/`
- logging/audit helpers under `src/DotnetDiagnosticsMcp.Server/`
- `docs/tool-reference.md`
- `docs/central-orchestrator-design.md` if design notes need status callouts
#### Test strategy
- unit tests for idle-expiry behavior,
- unit tests for `detach` idempotence,
- unit tests for restart visibility behavior,
- integration tests with a fake clock or equivalent time control,
- transport cleanup tests that assert the proxy closes and further calls fail with `SessionClosed` or `SessionNotFound`.
### P5 — RBAC manifests + Helm/Kustomize
#### Goal
Ship the deployment assets that make the orchestrator operable:
- ServiceAccount
- Role / RoleBinding by default
- optional ClusterRole examples behind explicit warnings
- deployment/service manifests
- Helm or Kustomize support if the repo wants packaged install surfaces
#### Dependencies
- P3 and P4 complete enough to justify deployment assets.
- Finalized label allowlist and namespace-scope defaults.
#### Expected file touch list
- `deploy/k8s/` new orchestrator manifests
- `deploy/k8s/README.md` or equivalent new walkthrough
- Helm chart or Kustomize overlays if chosen
- `README.md`
- `docs/client-setup.md`
#### Test strategy
- static manifest validation,
- minimal render tests for Helm/Kustomize if introduced,
- no full kind acceptance here unless the team wants to combine with P6.
### P6 — kind-based integration test
#### Goal
Meet the issue's acceptance criterion with a realistic cluster test:
- deploy two replicas of the sample target,
- attach to one specific Pod by label or exact name,
- run `inspect_process(view="list")` through the orchestrator,
- prove only the chosen Pod's PID is visible.
#### Dependencies
- P3 attach/proxy path.
- P4 session lifecycle.
- P5 deployment assets or test fixtures.
#### Expected file touch list
- `tests/` new kind-based integration coverage
- cluster test fixtures under `tests/fixtures/` or `deploy/k8s/test/`
- CI workflow updates if cluster tests are enabled in automation
- possibly sample manifests for multi-replica prepared targets
#### Test strategy
- bring up kind,
- deploy orchestrator,
- deploy prepared sample target with two replicas,
- call `list_orchestrator(kind="pods")`,
- call `attach_to_pod` for one chosen Pod,
- call `inspect_process(view="list")` through the orchestrator,
- assert the returned processes belong only to that Pod,
- call `detach`,
- assert the session disappears from `list_orchestrator(kind="investigations")`.
---
## 9. Open questions
1. **Concurrent attach sessions to the same Pod — supported or rejected by default?** Reuse is cleaner, but there may be valid multi-client cases.
2. **Should existing tools remain purely session-bound, or should they also grow an optional `investigationHandle` argument?** Session binding is cleaner; explicit handles are more flexible.
3. **Should the first production scope be namespace-only, or should cluster-wide deployment be supported in the first real release?** This document recommends namespace-first.
4. **How should the orchestrator detect and explain target preparedness?** Best-effort heuristic, explicit label contract, or both?
5. **Do we reuse an existing active attach automatically when the same Pod is requested, or must the caller opt into reuse every time?**
6. **What client identity should be captured in audit logs when the outer auth model remains bearer-only?** Raw token fingerprint, MCP client info, or both?
7. **Should we offer a lower-privilege EventPipe-only orchestrator profile in the first implementation, or defer that until after the full privileged path works?**
8. **How much attach latency is acceptable before the experience stops feeling interactive?** This needs measurement in kind and real clusters.
9. **How should stale post-restart ephemeral containers be surfaced to the user?** Logs only, metrics, or a warning row in `list_orchestrator(kind="pods")`?
10. **Should `list_orchestrator(kind="pods")` expose ReplicaSet/workload grouping in addition to raw Pod rows?** Raw rows are simpler; grouped rows may be friendlier for LLM selection.
11. **Should `attach_to_pod` require exact Pod name, or should workload-level selection be allowed later (`deployment=api`, `pick newest ready replica`)?** Exact Pod name is safer for the first release.
12. **Should the orchestrator live in the existing server binary or in a thin companion binary in the same repo?** Same binary is simpler to ship; a companion reduces Kubernetes coupling for local-only users.
---
## 10. References
### Primary repo references
- [`AGENTS.md`](../AGENTS.md)
- [`deploy/k8s/CENTRAL-TOPOLOGY.md`](../deploy/k8s/CENTRAL-TOPOLOGY.md)
- [`deploy/k8s/central-target.yaml`](../deploy/k8s/central-target.yaml)
- [`deploy/k8s/ephemeral-attach.patch.json`](../deploy/k8s/ephemeral-attach.patch.json)
- PR #137 (`docs/central-k8s-design.md` on that branch; complementary feasibility/design spike)
### Related issues
- [#15 feat(infra): central K8s topology (no per-pod sidecar)](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/issues/15)
- [#16 feat(infra): cloud platform integrations (App Service / ACA / ECS / Lambda)](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/issues/16)
- [#17 Phase 7 — Post-MVP Roadmap](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/issues/17)
- [#20 Central MCP orchestrator (multi-pod fleet)](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/issues/20)
- [#22 Cloud recipes: AWS ECS/Fargate + GCP Cloud Run](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/issues/22)
### Kubernetes references
- [Ephemeral Containers](https://kubernetes.io/docs/concepts/workloads/pods/ephemeral-containers/)
- [Debugging with an ephemeral container](https://kubernetes.io/docs/tasks/debug/debug-application/debug-running-pod/#ephemeral-container)
- [Port Forwarding](https://kubernetes.io/docs/tasks/access-application-cluster/port-forward-access-application-cluster/)
- [Configure a Security Context for a Pod or Container](https://kubernetes.io/docs/tasks/configure-pod-container/security-context/)
---
## Recommendation summary
Proceed with issue #20 as a **central orchestrator** feature, not as a new socket-attach mechanism.
The correct Phase-1 opinionated path is:
- prepared targets only,
- ephemeral debug container per investigation,
- namespace-scoped orchestrator by default,
- in-process kube client plus direct port-forward proxying,
- stateless session handles with TTL and reaping,
- and four new fleet-aware MCP tools that sit in front of the unchanged existing diagnostics tool surface.
That gives the LLM one fleet endpoint without undoing the constraints and wins already documented in the current on-demand topology.

---

## Running the kind integration test locally

The P6 acceptance test (`KindIntegrationTests`, decorated with
`[Trait("Category", "KindIntegration")]`) stands up two CoreClrSample replicas
on a kind cluster, attaches to one via the orchestrator's `attach_to_pod`, and
then calls `inspect_process(view="list")` through the per-handle reverse proxy. It
asserts exactly one PID is visible and that its
`ManagedEntrypointAssemblyName` is `CoreClrSample` — proving the proxy
forwarded the request into the chosen Pod's PID namespace and not the
sibling's.

The CI job in `.github/workflows/kind-integration.yml` is the source of truth.
To reproduce locally:

```bash
# 1. Build images
docker build -t dotnet-diagnostics-mcp:p6 -f deploy/Dockerfile .
docker build -t coreclr-sample:p6 -f samples/CoreClrSample/Dockerfile .

# 2. Create a kind cluster and load images
kind create cluster --name p6-kind
kind load docker-image dotnet-diagnostics-mcp:p6 --name p6-kind
kind load docker-image coreclr-sample:p6 --name p6-kind

# 3. Install the orchestrator
kubectl create namespace dotnet-diagnostics-mcp
helm install dotnet-dbg-mcp deploy/helm/dotnet-diagnostics-orchestrator \
  --namespace dotnet-diagnostics-mcp \
  --set image.repository=dotnet-diagnostics-mcp \
  --set image.tag=p6 \
  --set image.pullPolicy=IfNotPresent \
  --set bearerToken.value=kind-test-bearer-token \
  --set orchestrator.ephemeralContainerImage=dotnet-diagnostics-mcp:p6 \
  --set 'orchestrator.allowedNamespaces[0]'=p6-sample \
  --set 'orchestrator.labelKeyAllowlist[2]'=p6-target \
  --wait --timeout 3m

# 4. Apply the two-replica sample fixture
kubectl apply -f deploy/k8s/p6-sample/
kubectl -n p6-sample wait --for=condition=Available deploy/p6-sample-a --timeout=3m
kubectl -n p6-sample wait --for=condition=Available deploy/p6-sample-b --timeout=3m

# 5. Port-forward and run the test
kubectl -n dotnet-diagnostics-mcp port-forward \
  svc/dotnet-dbg-mcp-dotnet-diagnostics-orchestrator 5130:5130 &

DOTNET_DBG_MCP_KIND_TEST=1 \
DOTNET_DBG_MCP_ORCH_URL=http://127.0.0.1:5130 \
DOTNET_DBG_MCP_ORCH_TOKEN=kind-test-bearer-token \
DOTNET_DBG_MCP_KIND_NAMESPACE=p6-sample \
DOTNET_DBG_MCP_KIND_TARGET_LABEL=p6-target=a \
dotnet test tests/DotnetDiagnosticsMcp.Server.IntegrationTests/ -c Release \
  --filter "Category=KindIntegration"
```

Without `DOTNET_DBG_MCP_KIND_TEST=1` the test returns early (no-op pass) so
it is safe to leave in the standard test inventory; `ci.yml`'s server
integration step explicitly excludes `Category=KindIntegration` to keep the
no-op out of regular runs.
