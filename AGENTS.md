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

`collect_thread_snapshot`, `inspect_live_heap`, `inspect_dump` (against a live PID) and `collect_process_dump` attach via `ptrace(2)`. On Linux with `kernel.yama.ptrace_scope=1` (Debian/Ubuntu/WSL default) same-UID peer attach is blocked.

- **Docker (local)**: `--cap-add SYS_PTRACE` on the **sidecar** container.
- **K8s**: `capabilities.add: ["SYS_PTRACE"]` on the sidecar container's `securityContext`. See [`deploy/k8s/sample-sidecar.yaml`](./deploy/k8s/sample-sidecar.yaml).
- **Bare host**: `sudo sysctl -w kernel.yama.ptrace_scope=0`.

Failure surfaces as a structured `PermissionDenied` envelope (see #32). EventPipe-based tools (counters, cpu_sample, exceptions, gc, event_source) do **not** need `CAP_SYS_PTRACE`.

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
curl … & sleep 0; dotnet … collect_exceptions
# RIGHT — schedule load to happen during the collection window
( sleep 2; curl … ) &
dotnet … collect_exceptions  # synchronous
```

### 🧪 Live tests are real

`tests/DotnetDiagnosticsMcp.Core.Tests/LiveCoreClrProcessTests.cs` spawns the `CoreClrSample` webapi by invoking its published DLL directly (`dotnet …/CoreClrSample.dll`) and attaches to the resulting PID. The fixture deliberately avoids `dotnet run`, which creates a wrapper host process whose PID is not the application. Required: .NET 10 SDK on `PATH`, ability to bind to `127.0.0.1:0`, and ~10s of runtime. CI runs both Linux and Windows runners.

### 🎯 One MCP tool per concept (≤10 typical; document why each addition is essential)

Anthropic recommends ≤10 tools per LLM context. We currently have 20 (the Phase 7 additions —
`get_call_tree`, `start_investigation`, `export_investigation_summary`, `compare_to_baseline`,
`get_collection_status`, `cancel_collection`, `inspect_live_heap`, `query_heap_snapshot`,
`collect_thread_snapshot`, `query_thread_snapshot` — each unlock a specific drilldown or
workflow the LLM cannot otherwise reach). **Don't add tools
speculatively**. New capabilities should either:

1. Extend an existing tool with a parameter, or
2. Be exposed as a Resource (`audience=["assistant"]`) or Prompt, not a Tool, or
3. Follow the **"split collector, unified drilldown"** pattern: separate collectors per backend
   (e.g. `inspect_dump` vs `inspect_live_heap`) register a single `HeapSnapshotArtifact` in the
   shared `IDiagnosticHandleStore`, and a single `query_heap_snapshot(handle, view, …)` tool
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

## Things deliberately not in scope

- **Modifying the target application** — non-goal. Everything must work over the diagnostic socket.
- **Persistent server-side state** — server stays stateless; investigation memory is portable JSON the agent persists externally (see issue [#10](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/issues/10)).
- **A web UI** — this is an MCP server; the UI is whatever MCP client the human uses.
- **Replacing dotnet-monitor** — different goals. `dotnet-monitor` is rule-based collection for ops; we are interactive diagnosis for an LLM. They complement each other.
