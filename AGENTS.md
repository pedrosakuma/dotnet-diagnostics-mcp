# AGENTS.md

> Operating guide for AI coding agents (Copilot CLI, Codex, Claude Code, Cursor, etc.) working on this repository. Humans benefit too.

## What this project is

`dotnet-diagnostics-mcp` is an **MCP server** that lets an LLM perform on-demand performance diagnostics on running **.NET 10** applications — locally or in a Kubernetes sidecar — **without any modification to the target app**.

The server attaches to the .NET runtime diagnostic IPC socket and exposes 9 tools (process discovery, capability detection, EventCounters snapshot, CPU sampling, exception collection, GC events, EventSource passthrough, process dump) over either **Streamable HTTP** (default, with bearer-token auth — designed for sidecar / shared-deploy) or **stdio** (`--stdio`, recommended for local dev — the MCP client owns the process lifecycle, no daemon or bearer token; see issue #74).

**Status:** MVP complete (Phases 1–6). Active work on Phase 7 is tracked in [issue #17](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/issues/17) and the milestone [`Phase 7 — Roadmap`](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/milestone/1).

## Repository layout

```
src/
  DotnetDiagnosticsMcp.Core/      — IPC + EventPipe primitives, no MCP knowledge
  DotnetDiagnosticsMcp.Server/    — MCP tools, HTTP transport, bearer auth
tests/
  DotnetDiagnosticsMcp.Core.Tests/              — live process tests (spawn sample, attach, assert)
  DotnetDiagnosticsMcp.Server.IntegrationTests/ — HTTP + MCP protocol tests
samples/
  CoreClrSample/      — minimal ASP.NET API used by Core tests
  NativeAotSample/    — used to validate capability detection
  BadCodeSample/      — 7 anti-pattern endpoints to exercise the LLM diagnostic loop
deploy/
  Dockerfile          — multi-stage publish; copies .editorconfig for analyzers
  k8s/sample-sidecar.yaml — reference K8s topology
docs/
  README.md, tool-reference.md, investigation-playbooks.md,
  bad-code-scenarios.md, local-docker-sidecar.md, client-setup.md
```

## Build, test, run

All commands are run from the repo root.

```bash
# Build everything
dotnet build DotnetDiagnosticsMcp.slnx -c Release

# Run all tests (Core tests spawn a live sample process; require .NET 10 SDK on PATH)
dotnet test DotnetDiagnosticsMcp.slnx -c Release --no-build

# Run only the integration tests
dotnet test tests/DotnetDiagnosticsMcp.Server.IntegrationTests/ -c Release --no-build

# Run the MCP server locally (launch profile listens on http://localhost:5130)
dotnet run --project src/DotnetDiagnosticsMcp.Server -c Release

# Run a single live test
dotnet test tests/DotnetDiagnosticsMcp.Core.Tests/ -c Release --no-build \
  --filter FullyQualifiedName~Counters_ReturnsSystemRuntimeMetrics
```

The orchestrator's end-to-end kind acceptance test is gated by
`Category=KindIntegration` and runs only when the
`.github/workflows/kind-integration.yml` job (or the manual reproduction
documented in [`docs/central-orchestrator-design.md`](./docs/central-orchestrator-design.md))
sets `DOTNET_DBG_MCP_KIND_TEST=1` plus its companion env vars. The standard
`ci.yml` server-integration leg filters `Category!=KindIntegration` so it
never appears as a misleading "Passed" no-op there.

**Bearer token.** The server reads `MCP_BEARER_TOKEN` from the environment. If unset, it
generates an ephemeral 32-byte hex token at startup and logs it as a warning — there is no
hard-coded default. The local docker walkthroughs explicitly pass `MCP_BEARER_TOKEN=dev-token`.

**SDK version.** `global.json` pins `10.0.201` with `rollForward: latestFeature`. Use the
SDK from `global.json`, not whatever is on `PATH` outside this repo.

**Warnings as errors.** `Directory.Build.props` sets `TreatWarningsAsErrors=true` for source
projects (test projects opt out). Analyzer warnings (CA1711, CA1852, CA1861, etc.) will fail
the build — fix them, do not suppress them globally.

**Central package management.** Package versions live in `Directory.Packages.props`. Project
files reference packages without a `Version` attribute. Add new packages to the central props
first.

### Local Docker sidecar (matches K8s topology)

See [`docs/local-docker-sidecar.md`](./docs/local-docker-sidecar.md) for the canonical CoreClrSample
walkthrough, and [`docs/bad-code-scenarios.md`](./docs/bad-code-scenarios.md) for the BadCodeSample
walkthrough used to exercise the LLM diagnostic loop. Both use network `diagmcp-net`,
publish the MCP server on `127.0.0.1:18887`, and require `--user 0` on the sidecar (see UID
note below). Do not invent ad-hoc names or ports — drift between repos and docs is the #1
source of wasted debugging time here.

## Critical conventions you must respect

These are the easy-to-break things that have cost us debugging time before.

### 🔌 Diagnostic socket UID — same UID, every time

The .NET diagnostic IPC socket at `/tmp/dotnet-diagnostic-<pid>` inherits the **target app's UID**. The MCP sidecar must run with **the same UID** or it gets `ServerNotAvailableException: Permission denied`.

- **Local dev**: `docker run --user 0 …` on the sidecar (target runs as root).
- **K8s**: pod-level `securityContext` with matching `runAsUser` + `runAsGroup` + `fsGroup`. See [`deploy/k8s/sample-sidecar.yaml`](./deploy/k8s/sample-sidecar.yaml).

### 🪪 `CAP_SYS_PTRACE` for ClrMD-backed tools — UID alone is not enough on Linux

`collect_thread_snapshot`, `inspect_heap(source="live")`, `inspect_heap(source="dump")` (against a live PID) and `collect_process_dump` attach via `ptrace(2)`. On Linux with `kernel.yama.ptrace_scope=1` (Debian/Ubuntu/WSL default) same-UID peer attach is blocked.

- **Docker (local)**: `--cap-add SYS_PTRACE` on the **sidecar** container.
- **K8s**: `capabilities.add: ["SYS_PTRACE"]` on the sidecar container's `securityContext`. See [`deploy/k8s/sample-sidecar.yaml`](./deploy/k8s/sample-sidecar.yaml).
- **Bare host**: `sudo sysctl -w kernel.yama.ptrace_scope=0`.

Failure surfaces as a structured `PermissionDenied` envelope (see #32). EventPipe-based tools (counters, cpu_sample, exceptions, gc, event_source) do **not** need `CAP_SYS_PTRACE`.

### 🐧 WSL2 perf quirks (host-side off-CPU sampling)

On WSL2 the bundled `/usr/bin/perf` wrapper matches `$(uname -r)` (e.g. `6.6.114.1-microsoft`) against `/usr/lib/linux-tools/<ver>/perf` and fails when no exact match exists. The fix is to invoke the installed binary directly — e.g. `/usr/lib/linux-tools/6.8.0-117-generic/perf` — by exporting `PERF=` or by symlinking. Also note that `kernel.perf_event_paranoid=2` (the WSL default) blocks `sched:sched_switch` tracepoints required by `collect_off_cpu_sample` on the **host**; lower it (`sudo sysctl -w kernel.perf_event_paranoid=-1`) or run the workload **inside** a container with `--cap-add PERFMON`, which is the topology the docker / K8s sidecar recipes already use.

### 🐚 Shell escapes when driving `gh` / `git`

Three pitfalls that have eaten hours of debugging in past sessions:

- **`!` in titles**: `gh issue create --title "MEX !deadlock equivalent"` silently fails because bash history expansion runs inside double quotes. The command exits 0, nothing is created, no error printed. Use **single-quoted titles** for anything containing `!`, or `set +H` in the same shell first.
- **Inline `--body "..."` / `-m "..."` with markdown** (backticks, `$`, `!`, heredoc-like patterns) hangs or silently truncates. Always use `--body-file <path>` / `-F <path>` for non-trivial messages.
- **Don't pipe `gh ... create` output** (`| tail`, `| head`, `2>&1 | …`). On non-success paths `gh` produces no URL and the pipe masks the failure. Verify with `gh pr view` / `gh issue list --search` after every create.

When any of the above bites, the diagnostic signature is: `gh` exits 0, no URL on stdout, no resource on the server. Always confirm the resource exists before claiming the step completed.

### 🐳 `.dockerignore` must re-include `.editorconfig`

Our `.dockerignore` starts with `*` (deny-all) and re-includes specific paths. **If you remove the `!.editorconfig` line, the publish breaks** with CA1848/CA1873 errors because the analyzer suppressions live in `.editorconfig`. Same applies to `!samples/` for sample images.

### ⏱️ EventPipe session startup is not instant

EventPipe sessions take ~500ms–1s to fully start. Then `EventCounters` payloads arrive at `EventCounterIntervalSec` boundaries.

- Always give live tests **≥6s** of `duration` for counters with `intervalSeconds=1`.
- For exception/GC collection, **start the session BEFORE** the load that generates events.

```bash
# WRONG — exceptions collector starts after curl loop finishes
curl … & sleep 0; dotnet … collect_events(kind="exceptions")
# RIGHT — schedule load to happen during the collection window
( sleep 2; curl … ) &
dotnet … collect_events(kind="exceptions")  # synchronous
```

### 🧪 Live tests are real

`tests/DotnetDiagnosticsMcp.Core.Tests/LiveCoreClrProcessTests.cs` spawns the `CoreClrSample` webapi by invoking its published DLL directly (`dotnet …/CoreClrSample.dll`) and attaches to the resulting PID. The fixture deliberately avoids `dotnet run`, which creates a wrapper host process whose PID is not the application. Required: .NET 10 SDK on `PATH`, ability to bind to `127.0.0.1:0`, and ~10s of runtime. CI runs both Linux and Windows runners.

### 🎯 One MCP tool per concept (15 tools after RFC 0002 §7.3 alias removal)

Anthropic recommends ≤10 tools per LLM context. We have 15 tools after RFC 0002 §7.3 #213 consolidated
24 legacy aliases into 7 unified discriminator tools: `inspect_process`, `collect_events`, `collect_sample`,
`query_snapshot`, `inspect_heap`, `list_orchestrator`, `get_bytes` plus 8 non-aliased tools
(`collect_process_dump`, `collect_thread_snapshot`, `capture_method_bytes`, `start_investigation`,
`export_investigation_summary`, `compare_to_baseline`, `attach_to_pod`, `detach_from_pod`).
**Don't add tools speculatively**. New capabilities should either:

1. Extend an existing tool with a parameter, or
2. Be exposed as a Resource (`audience=["assistant"]`) or Prompt, not a Tool, or
3. Follow the **"split collector, unified drilldown"** pattern: separate collectors per backend
   (e.g. `inspect_heap(source="dump")` vs `inspect_heap(source="live")`) register a single `HeapSnapshotArtifact` in the
   shared `IDiagnosticHandleStore`, and a single `query_snapshot(handle, view, …)` tool
   answers parameterized follow-up questions. This keeps the tool surface flat while letting the
   LLM ask narrowly-scoped questions without re-paying the collection cost.

See Phase 7 issues [#8 `mcp-drilldown`](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/issues/8)
and [#24 wave 1 heap drilldown](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/issues/24).

## Phase 7 — what is being designed and why

All open work has a GitHub issue. **Read the meta tracking issue first**: [#17 Phase 7 — Post-MVP Roadmap](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/issues/17). It carries the current dependency graph, execution order, label taxonomy, and links to the two deep-research artifacts that back the design. Don't inline that taxonomy here — it drifts.

## How to contribute as an agent

When picking up an issue:

1. **Read the meta tracking issue** ([#17](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/issues/17)) for the dependency graph. Don't start blocked work.
2. **Read the linked research findings** in the issue body before designing.
3. **Build + run tests** before and after — `dotnet build` and `dotnet test` from the repo root.
4. **Don't add tools without an issue** — discuss first in the relevant `drilldown` or `discoverability` issue.
5. **Keep PRs small and reference the issue** (`Closes #N`).
6. **Don't commit** secrets, dumps (`*.dmp`), or `.nettrace` files.

### Agent workflow conventions

These are repo-wide meta-workflows that have proven to pay off here. They are
declarative on purpose — the goal is to bias decisions, not to script every
turn. Skip them when the task is genuinely trivial.

- **Mandatory code review before flipping a PR out of draft.** Use the
  `task` tool with `agent_type: "code-review"` and `model: "gpt-5.5"` against
  the staged / branch diff and address every real finding. Empirically this
  has caught a real bug on most non-trivial PRs in this repo — including
  several flake / race-condition regressions that the human + author-agent
  pair both missed.
- **Decompose-then-parallelise.** Features here tend to land as several small,
  independent PRs (RFC 0002 shipped as 13). When the work decomposes into ≥2
  independent trails (different directories, different test surfaces, no
  shared schema migration), prefer dispatching one background sub-agent per
  trail over serialising them in the main loop (in Copilot CLI: `task` with
  `mode: "background"`; other agent CLIs expose an equivalent). The main loop
  keeps coordination + code review; the sub-agents own implementation.
- **Pre-scope R&D items with a `research` or `explore` agent first.** For
  fuzzy / multi-week items (e.g. "NativeAOT heap walk", "new cloud provider
  recipe"), dispatch a sub-agent for survey + feasibility before drafting the
  plan. Saves the main context for actual design + execution.
- **Don't reach for a sub-agent when a single tool call would do.** Simple
  lookups (one grep, one file read), pointed edits, and any interactive
  debugging stay in the main loop — sub-agent fidelity loss is not worth it.
- **Worktree etiquette for parallel work.** Create the branch on the remote
  first (`git worktree add -b <branch> /tmp/<dir> origin/main`) so the sub-
  agent operates on an isolated checkout. After merge, `git worktree remove
  --force <path>` before `gh pr merge --delete-branch` (the latter cannot
  delete a local branch held by a worktree).

User- or task-scoped preferences ("for this PR don't run review", "I prefer
option X") belong in the prompt, not here. Conventions in this section apply
to every contributor and every agent on this repo.

## Things deliberately not in scope

- **Modifying the target application** — non-goal. Everything must work over the diagnostic socket.
- **Persistent server-side state** — server stays stateless; investigation memory is portable JSON the agent persists externally (see issue [#10](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/issues/10)).
- **A web UI** — this is an MCP server; the UI is whatever MCP client the human uses.
- **Replacing dotnet-monitor** — different goals. `dotnet-monitor` is rule-based collection for ops; we are interactive diagnosis for an LLM. They complement each other.
