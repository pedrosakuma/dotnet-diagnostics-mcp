# Consumer installation guide

This page covers installing **dotnet-diagnostics-mcp** as an end user — no source clone, no .NET SDK on PATH (unless you pick the global-tool path), and no manual restart on crash / reboot.

> Looking for the contributor walkthrough (clone, build from source, share a single dev instance across multiple terminals)? See [README → Contributor setup](../README.md#contributor-setup) and `scripts/local-mcp.sh`.

---

## 1. Pick a distribution

| Distribution            | When to use it                                                                                        | Requires             |
|-------------------------|-------------------------------------------------------------------------------------------------------|----------------------|
| **.NET global tool**    | You already have .NET 10 SDK installed and want a managed install + upgrade path (`dotnet tool update`). | .NET 10 SDK          |
| **Container image**     | You want everything (sidecar parity with K8s, predictable filesystem, `--restart unless-stopped`).    | Docker / Podman      |
| **Single-file binary**  | You want zero runtime dependencies — drop one file on PATH and go.                                    | Nothing              |

All three publish the same MCP surface (Streamable HTTP, bearer-token authenticated, `/health` allow-listed).

> 🐧 **Linux heads-up — ClrMD-backed tools need ptrace.** Whichever distribution you pick, `collect_thread_snapshot`, `inspect_live_heap`, `inspect_dump` (against a live PID) and `collect_process_dump` will fail on Linux with `PermissionDenied` / `Could not PTRACE_ATTACH to any thread of the process N.` unless you grant the server permission to attach. Matching the target's UID is **not** enough on Debian/Ubuntu/WSL (default `kernel.yama.ptrace_scope=1`). See [§ 1.5 Linux: enabling ClrMD-backed tools](#15-linux-enabling-clrmd-backed-tools-ptrace) before you wire the server into your client. EventPipe-only tools (`snapshot_counters`, `collect_cpu_sample`, `collect_exceptions`, `collect_gc_events`, `collect_event_source`) work out of the box **unless** you opt into `collect_cpu_sample(resolveMethodInstantiations=true)`, which intentionally takes the ClrMD path to recover closed generic method signatures.

### 1a. .NET global tool

```bash
dotnet tool install -g dotnet-diagnostics-mcp
dotnet-diagnostics-mcp --urls http://127.0.0.1:8787
```

Upgrade: `dotnet tool update -g dotnet-diagnostics-mcp`. Uninstall: `dotnet tool uninstall -g dotnet-diagnostics-mcp`.

> **Renamed in v0.2.2.** The NuGet package id was `DotnetDiagnosticsMcp.Server` for 0.1.0–0.2.1 and is now `dotnet-diagnostics-mcp` (matches the tool command and the sibling `dotnet-assembly-mcp`). If you have the old id installed, run `dotnet tool uninstall -g DotnetDiagnosticsMcp.Server` first, then install the new one. The legacy id has been unlisted on NuGet.org.

### 1b. Container

```bash
docker run -d \
  --name dotnet-diagnostics-mcp \
  --restart unless-stopped \
  -p 127.0.0.1:8787:8787 \
  -e MCP_BEARER_TOKEN=$(openssl rand -hex 32) \
  ghcr.io/pedrosakuma/dotnet-diagnostics-mcp:latest
```

Attaching to a **live local process** from inside the container requires UID parity + a shared `/tmp` mount — see [docs/local-docker-sidecar.md](./local-docker-sidecar.md) for the canonical walkthrough.

### 1c. Single-file binary

Grab the per-OS archive from the [GitHub Releases](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/releases) page (`linux-x64`, `linux-arm64`, `win-x64`, `win-arm64`, `osx-arm64`), extract, and place `dotnet-diagnostics-mcp` on PATH.

```bash
tar -xzf dotnet-diagnostics-mcp-*-linux-x64.tar.gz -C ~/.local/bin
~/.local/bin/dotnet-diagnostics-mcp --urls http://127.0.0.1:8787
```

### 1.5. Linux: enabling ClrMD-backed tools (ptrace)

Four tools attach to the target via `ptrace(PTRACE_ATTACH, …)`:

- `collect_thread_snapshot`
- `inspect_live_heap`
- `inspect_dump` against a **live** PID (offline dump analysis is unaffected)
- `collect_process_dump`

Linux's [Yama LSM](https://www.kernel.org/doc/Documentation/admin-guide/LSM/Yama.rst) defaults `kernel.yama.ptrace_scope=1` on Debian, Ubuntu, WSL, GitHub Codespaces, and most desktop distros — meaning **same-UID peer attach is blocked**. The MCP server reports this as a structured `DiagnosticError`:

```json
{ "error": { "kind": "PermissionDenied",
             "message": "Could not PTRACE_ATTACH to any thread of the process N. Either the process has exited or you don't have permission." } }
```

Pick the recipe that matches your distribution:

| Distribution        | Recipe                                                                                       | Scope                  |
|---------------------|----------------------------------------------------------------------------------------------|------------------------|
| **Global tool / single-file binary** (running on the host) | `sudo sysctl -w kernel.yama.ptrace_scope=0`<br/>Persist with `echo 'kernel.yama.ptrace_scope = 0' \| sudo tee /etc/sysctl.d/10-ptrace.conf`. | Host-wide (relaxes a security default — see note below). |
| **Container (Docker / Podman)** | Add `--cap-add SYS_PTRACE` to the `docker run` command. | Sidecar container only. |
| **Container in compose** | Add `cap_add: [SYS_PTRACE]` to the service. The shipped [`deploy/docker-compose.yml`](../deploy/docker-compose.yml) already does this. | Service only. |
| **Kubernetes** | `securityContext.capabilities.add: ["SYS_PTRACE"]` on the **sidecar** container. The shipped [`deploy/k8s/sample-sidecar.yaml`](../deploy/k8s/sample-sidecar.yaml) already does this. | Sidecar only. |

> **Security note on `ptrace_scope=0`.** This is the historical Linux default and is appropriate for personal dev workstations / Codespaces. It lets any process owned by your UID attach to any other process owned by your UID — which is precisely what the diagnostics server needs. On a shared host or anything close to production, prefer the container/K8s recipes (capability scoped to the sidecar) over relaxing the host setting.

You can verify the current Yama policy with `cat /proc/sys/kernel/yama/ptrace_scope` — `0` allows the attach, `1` is "scope to children", `2` is "admin-only", `3` is "no attach". Anything > 0 will break the four tools above.

To dodge the requirement entirely, use the dump-based workflow:

```text
collect_process_dump  (runs inside the target process — no ptrace needed)
   ↓
inspect_dump          (offline analysis — no live attach)
```

`collect_process_dump` writes the dump via the diagnostic IPC socket, which only needs UID parity. The capture happens inside the target itself, so ptrace permission never enters the picture.

---

## 2. Run it as a supervised service

The server is stateless and resumable but you don't want to remember to restart it after every reboot or crash. The repo ships supervisor templates under [`deploy/supervisors/`](../deploy/supervisors).

### Linux — systemd `--user`

```bash
mkdir -p ~/.config/systemd/user
curl -sSL https://raw.githubusercontent.com/pedrosakuma/dotnet-diagnostics-mcp/main/deploy/supervisors/linux/dotnet-diagnostics-mcp.service \
  -o ~/.config/systemd/user/dotnet-diagnostics-mcp.service
# Edit the Environment=MCP_BEARER_TOKEN line before enabling.
$EDITOR ~/.config/systemd/user/dotnet-diagnostics-mcp.service
systemctl --user daemon-reload
systemctl --user enable --now dotnet-diagnostics-mcp.service

# Optional — keep the unit running after logout:
loginctl enable-linger "$USER"
```

Status: `systemctl --user status dotnet-diagnostics-mcp`. Logs: `journalctl --user -u dotnet-diagnostics-mcp -f`.

### Windows — Scheduled Task

```powershell
dotnet tool install -g dotnet-diagnostics-mcp
# Then run the supervisor script (downloaded from the release page or repo):
.\deploy\supervisors\windows\Install-Service.ps1 -Port 8787
```

The script registers a Scheduled Task that starts at logon, restarts on failure 5 times at 30s intervals, and publishes the bearer token as a user-scope environment variable.

> 🔒 **Need off-CPU sampling on Windows?** `collect_off_cpu_sample` uses the NT Kernel
> Logger's `ContextSwitch` provider, which requires Administrator membership or
> `SeSystemProfilePrivilege` — neither is held by the per-user Scheduled Task. For
> production sidecar deployments that want off-CPU, see
> [`windows-sidecar-service.md`](./windows-sidecar-service.md) (Windows Service install with
> `LocalSystem` or a dedicated least-privilege service account). Every other tool
> (counters, CPU sampling, exceptions, GC, EventSources, ETW NativeAOT CPU sampling) works
> from the Scheduled Task without changes.

Uninstall: `Unregister-ScheduledTask -TaskName 'dotnet-diagnostics-mcp' -Confirm:$false`.

### macOS — launchd `LaunchAgent`

```bash
cp deploy/supervisors/macos/io.github.pedrosakuma.dotnet-diagnostics-mcp.plist \
  ~/Library/LaunchAgents/
sed -i '' "s|{{HOME}}|$HOME|g; s|{{MCP_BEARER_TOKEN}}|$(openssl rand -hex 32)|g" \
  ~/Library/LaunchAgents/io.github.pedrosakuma.dotnet-diagnostics-mcp.plist
launchctl bootstrap gui/$UID ~/Library/LaunchAgents/io.github.pedrosakuma.dotnet-diagnostics-mcp.plist
launchctl enable gui/$UID/io.github.pedrosakuma.dotnet-diagnostics-mcp
```

### Container (already covered)

The `--restart unless-stopped` flag in the `docker run` recipe above is the resilience story for the container path. The image also defines a `HEALTHCHECK` that invokes `dotnet-diagnostics-mcp --health-check`.

---

## 3. Wire it into your MCP client

Add this to your `mcp-config.json` (Claude Desktop, Claude Code, Copilot CLI, Cursor — same shape, slightly different file location):

```json
{
  "mcpServers": {
    "dotnet-diagnostics": {
      "type": "http",
      "url": "http://127.0.0.1:8787/mcp",
      "headers": {
        "Authorization": "Bearer $MCP_BEARER_TOKEN"
      }
    }
  }
}
```

---

## 4. Optional — pair with `dotnet-assembly-mcp`

The diagnostics server resolves PDBs locally and stamps `SourceLocation` directly onto every `MethodIdentity` it emits for CPU samples (see [#28](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/issues/28)). That means **in a dev environment** where the source tree is open in your editor, `dotnet-diagnostics-mcp` alone is enough to follow a hotspot to its source line.

The partner [`pedrosakuma/dotnet-assembly-mcp`](https://github.com/pedrosakuma/dotnet-assembly-mcp) remains the right call for:

- Stripped binaries / NativeAOT (no PDB, no inline source).
- Third-party assemblies you don't have source for.
- Decompilation (`decompile_method`) and call-graph queries (`find_callers`).

When you want it, install side-by-side on a distinct port:

```bash
dotnet tool install -g dotnet-assembly-mcp
dotnet-assembly-mcp --urls http://127.0.0.1:8788
```

And add a second entry to `mcp-config.json`:

```json
{
  "mcpServers": {
    "dotnet-diagnostics": {
      "type": "http",
      "url": "http://127.0.0.1:8787/mcp",
      "headers": { "Authorization": "Bearer $MCP_BEARER_TOKEN" }
    },
    "dotnet-assembly": {
      "type": "http",
      "url": "http://127.0.0.1:8788/mcp",
      "headers": { "Authorization": "Bearer $MCP_BEARER_TOKEN" }
    }
  }
}
```

---

## 5. Verify

The CLI bundles a probe-only mode that exits 0 on a healthy 200 response from `/health` and 1 on any failure:

```bash
dotnet-diagnostics-mcp --health-check --urls http://127.0.0.1:8787
```

That same flag is what the systemd `ExecStartPost`, the Scheduled Task readiness gate, and the container `HEALTHCHECK` invoke under the hood.
