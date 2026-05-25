# Central Kubernetes topology design

_Status: Phase 1 spike for [issue #15](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/issues/15)._

This document answers one question: **can `dotnet-diagnostics-mcp` move from one sidecar per Pod to a central Kubernetes topology without changing target app code?**

Short answer: **yes, but only if the central topology launches a per-investigation attach surface close to the target Pod**. A permanently central process cannot safely or cheaply reach arbitrary Pods' diagnostic sockets by itself.

## 1. Context and goals

Today the repository documents two Kubernetes shapes:

- **Always-on sidecar**: `deploy/k8s/sample-sidecar.yaml`
- **On-demand attach**: `deploy/k8s/CENTRAL-TOPOLOGY.md`

The sidecar model is operationally simple, but it has three costs:

1. **Recurring Pod overhead**: every candidate Pod pays for an extra container, memory reservation, probes, and auth surface.
2. **Pod mutation**: application teams must accept a diagnostics container in every workload template.
3. **Lifecycle coupling**: diagnostics rollouts are tied to app Pod rollouts.

For issue #15, "central topology" means:

- a human or MCP client talks to **one logical diagnostics entrypoint** per namespace or cluster scope,
- the target application Pod runs **without a permanent diagnostics sidecar**,
- the attach path still works against the existing .NET diagnostic socket model,
- follow-up implementation can stay consistent with the current MCP tool surface and the current on-demand recipe.

## 2. Goals and non-goals

### Goals

- Validate whether a central topology is feasible on Kubernetes.
- Choose an architecture path for follow-up PRs.
- Document tradeoffs honestly enough to decide whether issue #15 is worth implementing.
- Keep Phase 1 to design only: **no server-tool additions and no deploy-manifest changes in this PR**.

### Non-goals

- Solving multi-cluster routing.
- Replacing the current per-Pod sidecar recipe.
- Supporting completely unprepared target Pods in the first implementation.
- Defining the full auth model for a shared central server; that is tracked separately.

## 3. Constraints

These constraints are hard because they come from the runtime or Kubernetes, not from repo-local preference.

### 3.1 Diagnostic socket locality and permissions

On Linux, the .NET runtime exposes a Unix socket at `/tmp/dotnet-diagnostic-<pid>`.

Implications:

- the process that opens the socket must be able to see the target PID,
- it must also see the same socket path,
- and it must have compatible filesystem permissions.

Per `AGENTS.md`, the socket inherits the **target process UID**. A diagnostics process running under a different UID gets `Permission denied`.

### 3.2 ClrMD-backed tools need extra privilege on Linux

`collect_thread_snapshot`, `inspect_heap(source="live")`, `inspect_heap(source="dump")` against a live PID, and `collect_process_dump` attach via `ptrace(2)`.

Per `AGENTS.md`, on hosts with `kernel.yama.ptrace_scope=1` the diagnostics container needs `CAP_SYS_PTRACE` even when the UID already matches.

### 3.3 Pod boundaries still matter in a "central" design

A Pod outside the target Pod does **not** automatically share:

- the target PID namespace,
- the target mount namespace,
- the target `/tmp`, or
- the target UID/GID configuration.

That means a single remote service cannot simply dial `/tmp/dotnet-diagnostic-<pid>` on arbitrary Pods across the cluster.

### 3.4 Performance target

The current sidecar path is effectively **sub-second per tool call after the MCP session exists**.

A central design should preserve that steady-state behavior. It is acceptable to pay a **one-time attach cost per investigation** if later tool calls go through a local-in-Pod MCP server or equivalent low-latency path.

This rules out designs that re-enter the Kubernetes API or spawn a fresh attach primitive for **every** MCP tool call.

### 3.5 Security and blast radius

The auth boundary of this product is already equivalent to shell-like diagnostic access to the target process.

A central topology must not silently widen that boundary to:

- every Pod on every node,
- every namespace by default, or
- privileged host access when a narrower Pod-scoped attach can do the job.

## 4. Feasibility summary

A central topology is feasible **if we split the problem in two layers**:

1. **Control plane**: a long-running central MCP entrypoint that knows how to pick a target `(namespace, pod, container)` and manage investigation sessions.
2. **Data plane**: an attach primitive that runs close enough to the target Pod to satisfy the diagnostic-socket and ptrace constraints.

That immediately narrows the viable path:

- the best attach primitive is **an ephemeral debug container per investigation**,
- the best control-plane shape is **namespace-scoped first**, not cluster-wide first,
- a node-wide DaemonSet is feasible but over-privileged for Phase 1,
- a namespace service that still injects something into the target Pod is useful, but only **as an orchestrator around the ephemeral attach**.

## 5. Options considered

## Option A: Ephemeral debug container per investigation

**Shape**

- Keep target Pods "prepared" as described in `deploy/k8s/CENTRAL-TOPOLOGY.md`:
  - shared `emptyDir` mounted at `/tmp`
  - fixed UID/GID or compatible `fsGroup`
- When an investigation starts, patch `pods/ephemeralcontainers` or use `kubectl debug --target`.
- The ephemeral container runs `dotnet-diagnostics-mcp` inside the target Pod context.
- A central service or operator then port-forwards/proxies traffic to that in-Pod MCP server for the lifetime of the investigation.

**Why it fits the constraints**

- `targetContainerName` gives the ephemeral container the target's PID view.
- mounting the same `diag-tmp` volume at `/tmp` gives it the socket path.
- matching `runAsUser` / `runAsGroup` solves the UID rule.
- `CAP_SYS_PTRACE` can be added only when ClrMD-backed tools are needed.

**Pros**

- Best fit with the repo's existing on-demand recipe and docs.
- No permanent sidecar on every Pod.
- Per-investigation blast radius is limited to the chosen Pod.
- Keeps steady-state tool latency low after attach because the MCP server runs in-Pod, next to the target.
- Uses Kubernetes-native APIs that are stable in v1.25+.
- Lets us build a central orchestrator later without changing the attach primitive.

**Cons**

- The target Pod must still be prepared up front; this is not zero-touch for arbitrary workloads.
- Attach is slower than the current always-on sidecar because it includes patching, scheduling, image availability, and readiness wait.
- Ephemeral containers cannot be removed; cleanup usually means ending the session and letting the Pod age out or be restarted.
- Kubernetes does not allow `resources` on ephemeral containers, so the diagnostics attach cannot carry the same per-container CPU/memory limits as the always-on sidecar. That increases noisy-neighbor risk for heavy captures and makes operator guidance more important.
- RBAC is sensitive: `pods/ephemeralcontainers`, `pods`, and likely `pods/portforward` are powerful permissions.
- If the diagnostics image is not already on the node, first attach pays image-pull latency.

**Operational note**

This option is acceptable only if attach is **once per investigation**, not once per tool call. The central service must return a session handle and reuse the same in-Pod MCP server. Follow-up docs should also steer operators toward low-impact collectors first, because the ephemeral attach cannot rely on container-level `resources.limits` the way the permanent sidecar can.

## Option B: One diagnostics Pod per node (DaemonSet)

**Shape**

- Deploy `dotnet-diagnostics-mcp` as a DaemonSet.
- Give it `hostPID: true` and enough host visibility to discover target processes and their filesystems.
- Reach target sockets through host-level namespace traversal such as `/proc/<pid>/root/tmp` and use host privileges when ptrace is required.

**Pros**

- Lower recurring cost than per-Pod sidecars.
- Potentially no Pod-template mutation for application workloads.
- Always-on presence avoids ephemeral-container startup latency.

**Cons**

- Highest privilege of the options considered.
- Cross-Pod `/tmp` access is not naturally available; in practice this becomes a host-namespace and `/proc/<pid>/root` design, which is much closer to node debugging than Pod debugging.
- Stronger dependence on container-runtime and host-kernel behavior.
- Harder security story: a compromised daemon effectively becomes a node-level diagnostic agent.
- Much harder to scope cleanly to a namespace.
- Conflicts with the current repo guidance that unprepared-target attach is future work and likely requires root plus ptrace.

**Assessment**

Feasible, but it solves issue #15 by taking on a much larger privilege and threat-model burden than we need.

## Option C: One diagnostics service per namespace with an injector/orchestrator pattern

**Shape**

- Run one long-lived `dotnet-diagnostics-mcp`-adjacent service per namespace.
- The service lists candidate Pods and starts/stops per-Pod investigation sessions on demand.
- The service does **not** talk to the .NET socket directly; it launches or patches the actual attach surface into the target Pod.

**Pros**

- Gives users the "single endpoint" experience that issue #15 wants.
- Namespace scoping is a strong default blast-radius boundary.
- Good operational fit for shared clusters where platform teams own diagnostics for one namespace at a time.
- Natural place to centralize session bookkeeping, attach reuse, audit logging, and future auth.

**Cons**

- By itself, it is **not** an attach primitive.
- If implemented by mutating Deployments or injecting long-lived sidecars, it recreates much of the lifecycle coupling we want to remove.
- Adds control-plane complexity before we solve auth and multi-client ownership questions.

**Assessment**

Useful as the **control plane**, but only when paired with Option A as the data plane.

## Option D: Status quo per-Pod sidecar

**Pros**

- Already implemented and documented.
- Lowest attach latency.
- Simplest operational model.

**Cons**

- Pays the recurring cost on every Pod.
- Requires app-pod mutation and ongoing coordination with workload owners.
- Does not satisfy issue #15.

**Assessment**

Baseline only; keep as a supported topology, not as the answer to this issue.

## 6. Recommendation

**Recommend: Option A as the attach primitive, delivered behind Option C as a namespace-scoped central orchestrator.**

In plainer terms:

- **Do not** build a node-level DaemonSet first.
- **Do not** invent a new remote attach mechanism in Phase 2.
- **Do** reuse the prepared-target + ephemeral-container model already documented in `deploy/k8s/CENTRAL-TOPOLOGY.md`.
- **Do** put a single central MCP-facing service in front of it later so users target `(namespace, pod, container)` instead of manually patching Pods.

### Why this is the best path

1. **It is the smallest step from what already works.** The repo already documents the on-demand attach recipe. We are extending that model, not replacing it.
2. **It respects the hard socket constraints.** The diagnostics process still runs in the target Pod context, where PID, `/tmp`, and UID alignment are tractable.
3. **It preserves good steady-state latency.** Attach cost is paid once, then the in-Pod MCP server handles the diagnostic loop with sidecar-like responsiveness.
4. **It keeps privileges narrower than a node agent.** Namespace-scoped RBAC plus Pod-scoped ephemeral attach is easier to explain and audit than `hostPID` plus host traversal.
5. **It leaves room for future expansion.** If later work proves that cluster-scoped orchestration is needed, we can widen the orchestrator's scope without rewriting the attach mechanism.

### Architecture path chosen

Phase 1 should explicitly choose this path:

> **Namespace-scoped central orchestrator + per-investigation ephemeral debug container attached to a prepared target Pod.**

That means issue #15 is **feasible**, but the first implementation should intentionally inherit these constraints:

- targets must be prepared for shared `/tmp` and UID alignment,
- attach lifetime is investigation-scoped, not request-scoped,
- central orchestration is the UX layer, not the transport that touches the socket directly.

## 7. Expected control/data-plane flow

1. User connects to the central MCP endpoint.
2. User or agent selects `target = (namespace, pod, container)`.
3. Central service validates the target Pod is prepared enough for attach.
4. Central service patches `pods/ephemeralcontainers` with the diagnostics image and desired security context.
5. Central service waits for the ephemeral container to start.
6. Central service opens a transport to the in-Pod MCP server and returns a session handle.
7. Existing diagnostics calls execute against that session until the user detaches or the Pod disappears.

The important design choice is step 6: after attach, the central server should behave like a **session multiplexer**, not like a fresh launcher for every call.

## 8. Phasing

The implementation should be split into follow-up PRs.

### Phase 2: pod target abstraction

**Goal**

Introduce an explicit target concept such as `target = (namespace, pod, container)` as an opt-in argument or session-scoped selection model.

**Depends on**

- Phase 1 design decision from this document.
- Alignment with the tool-surface question tracked in open questions below.

**Expected file touch list**

- `src/DotnetDiagnosticsMcp.Server/` tool argument models and request plumbing
- `src/DotnetDiagnosticsMcp.Core/` abstractions for remote target resolution if needed
- `docs/tool-reference.md`
- `docs/client-setup.md` if examples need target-aware flows

**Test strategy**

- Unit tests around argument validation and backward compatibility.
- Integration tests proving existing local and sidecar flows still work when `target` is omitted.

### Phase 3: ephemeral container launcher and RBAC

**Goal**

Implement the Kubernetes-facing launcher that turns a selected target Pod into an investigation session.

**Depends on**

- Phase 2 target abstraction.
- A decision on whether the central service lives in this repo or as a thin companion process.

**Expected file touch list**

- `src/DotnetDiagnosticsMcp.Server/` orchestration layer or attach service
- possible new `src/...Kubernetes...` integration project if separation helps
- `deploy/k8s/` RBAC examples
- `docs/central-k8s-design.md` for status updates

**Test strategy**

- Unit tests for patch generation and readiness polling.
- Mocked or fake Kubernetes API tests for attach lifecycle.
- No kind dependency yet if we can keep this phase fast and deterministic.

### Phase 4: deploy manifests and walkthrough docs

**Goal**

Ship the first end-to-end namespace-scoped central topology recipe.

**Depends on**

- Phase 3 attach lifecycle working end to end.
- A basic auth story for the central endpoint, even if minimal.

**Expected file touch list**

- new `deploy/k8s/` manifests for the orchestrator deployment, service account, role, and role binding
- `deploy/k8s/README.md`
- possibly a new walkthrough under `docs/` or `deploy/k8s/`

**Test strategy**

- Static manifest validation.
- Human-run walkthrough against kind or Docker Desktop.
- Smoke test: attach to one sample Pod, call `inspect_process(view="list")`, verify session scoping.

### Phase 5: kind-based integration test (optional but recommended)

**Goal**

Prove the full control/data-plane loop inside CI-like conditions.

**Depends on**

- Phase 4 manifests and launcher being stable enough to justify cluster-test cost.

**Expected file touch list**

- `tests/` kind or cluster integration project/scripts
- CI wiring if enabled
- test-specific sample manifests under `deploy/` or `tests/fixtures/`

**Test strategy**

- Bring up a kind cluster.
- Deploy two target replicas in one namespace.
- Attach to exactly one Pod by selector or explicit name.
- Run `inspect_process(view="list")` and one cheap diagnostic call through the orchestrator.
- Assert the returned process set belongs only to the chosen Pod.

## 9. Tradeoffs we are explicitly accepting

This recommendation does **not** mean "central" is free.

We are explicitly accepting:

- a one-time attach delay per investigation,
- prepared-target requirements in the first implementation,
- namespace-scoped RBAC and operational ownership,
- session management complexity in the orchestrator,
- weaker per-attach resource isolation than the sidecar topology because ephemeral containers cannot declare `resources`.

We are explicitly **not** accepting in the first implementation:

- node-level host privileges as the default model,
- direct socket access from a remote service outside the target Pod,
- per-tool-call Kubernetes attach overhead,
- speculative new MCP tools before the target/session model is clear.

## 10. Open questions

1. **How should target selection appear in the MCP surface?**
   - add `target` to every existing tool,
   - add one `attach_to_pod` step that returns a scoped session,
   - or support both?
2. **How does the central MCP server authenticate and authorize multiple clients?**
   This overlaps with [issue #20](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/issues/20) and the broader shared-endpoint auth problem.
3. **Should the first orchestrator scope be one namespace or one cluster?**
   This document recommends namespace-first, but the product boundary should be explicit.
4. **Do we require prepared targets in documentation only, or do we also detect and explain missing `/tmp` / UID prerequisites at runtime?**
5. **What is the cleanup UX for long-lived ephemeral containers?**
   Since they cannot be removed, should the orchestrator mark sessions closed and rely on Pod restart policy, or should it also surface "pod now contains stale diagnostics container" warnings?
6. **Do we need a separate narrow RBAC profile for EventPipe-only investigations vs ptrace-capable investigations?**
   A split capability model would reduce default privilege but adds complexity.
7. **Should the orchestrator live in the same binary/repo surface as the current MCP server, or as a thin companion service?**
   Same binary is simpler to ship; a companion may keep the base server less Kubernetes-specific.
8. **How much attach latency is acceptable before the UX stops feeling "interactive"?**
   This should be measured during implementation, especially when the diagnostics image is cold on the node.

## 11. Decision

For Phase 1, the decision is:

- **Proceed with central-topology design work.**
- **Use ephemeral debug containers as the only recommended attach primitive.**
- **Treat a namespace-scoped orchestrator as the first central deployment shape.**
- **Defer implementation to follow-up PRs.**
- **Keep issue #15 open** until the follow-up phases land.

## 12. References

### Repo references

- [`AGENTS.md`](../AGENTS.md) — diagnostic socket UID requirement, `CAP_SYS_PTRACE`, tool-surface guidance
- [`deploy/k8s/sample-sidecar.yaml`](../deploy/k8s/sample-sidecar.yaml) — current always-on sidecar topology
- [`deploy/k8s/README.md`](../deploy/k8s/README.md) — sidecar and on-demand topology overview
- [`deploy/k8s/CENTRAL-TOPOLOGY.md`](../deploy/k8s/CENTRAL-TOPOLOGY.md) — existing on-demand ephemeral attach recipe
- [`deploy/k8s/central-target.yaml`](../deploy/k8s/central-target.yaml) — prepared target example
- [`deploy/k8s/ephemeral-attach.patch.json`](../deploy/k8s/ephemeral-attach.patch.json) — sample ephemeral-container patch

### Issue references

- [#15 feat(infra): central K8s topology (no per-pod sidecar)](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/issues/15)
- [#16 feat(infra): cloud platform integrations](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/issues/16)
- [#20 Central MCP orchestrator (multi-pod fleet)](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/issues/20)
- [#22 Cloud recipes: AWS ECS/Fargate + GCP Cloud Run](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/issues/22)

### Kubernetes references

- [Ephemeral Containers](https://kubernetes.io/docs/concepts/workloads/pods/ephemeral-containers/)
- [Debugging with an ephemeral container](https://kubernetes.io/docs/tasks/debug/debug-application/debug-running-pod/#ephemeral-container)
- [Share Process Namespace between Containers in a Pod](https://kubernetes.io/docs/tasks/configure-pod-container/share-process-namespace/)
- [Configure a Security Context for a Pod or Container](https://kubernetes.io/docs/tasks/configure-pod-container/security-context/)
- [Linux kernel security features in Kubernetes](https://kubernetes.io/docs/concepts/security/linux-kernel-security-constraints/)
