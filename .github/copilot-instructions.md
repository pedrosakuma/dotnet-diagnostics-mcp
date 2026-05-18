# GitHub Copilot instructions

The canonical agent guide for this repository is **[`AGENTS.md`](../AGENTS.md)** at the repo root. Read it before making changes — it covers conventions (diagnostic socket UID, `.dockerignore` re-includes, EventPipe timing, ≤10 MCP tools), the Phase 7 roadmap, and the full build/test surface.

## Critical facts (do not re-derive these)

- **SDK** is pinned by `global.json` to `10.0.201` (`rollForward: latestFeature`). Use that SDK.
- **Build:** `dotnet build DotnetDiagnosticsMcp.slnx -c Release`
- **Test:** `dotnet test DotnetDiagnosticsMcp.slnx -c Release --no-build` (live Core tests spawn a real sample process and need ~10s each)
- **Warnings are errors** in source projects (`Directory.Build.props` → `TreatWarningsAsErrors=true`). Fix analyzer warnings; do not suppress globally.
- **Central package management**: package versions live in `Directory.Packages.props`; project files reference packages without a `Version` attribute.
- **Bearer token**: server reads `MCP_BEARER_TOKEN`; if unset, generates and logs an ephemeral token. No hard-coded default.
- **Diagnostic socket UID**: MCP sidecar must run as the same UID as the target app (locally `--user 0`). Otherwise `ServerNotAvailableException: Permission denied`.

## Where to work

- [Phase 7 tracking issue #17](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/issues/17) — dependency graph + execution order
- [`docs/`](../docs) — tool reference, investigation playbooks, sidecar walkthroughs
