# Windows: running `dotnet-diagnostics-mcp` as a privileged service

> Companion guide to [`consumer-install.md`](./consumer-install.md). The Scheduled-Task
> installer documented there is the right default for **dev workstations** — it runs the
> server at logon under your own user account, no elevation, no admin prompt. This guide
> covers the **production sidecar** case where you also want **off-CPU sampling**
> (`collect_off_cpu_sample`), which requires a privilege the Scheduled-Task user typically
> does not have.

---

## 1. Why a Service (and not the Scheduled Task)?

Off-CPU sampling on Windows uses the NT Kernel Logger's `ContextSwitch` provider. There is
**no user-mode API** that exposes system-wide context switches without privilege — every
Windows off-CPU profiler (PerfView, xperf, WPR, `dotnet-trace` with kernel events) hits the
same wall.

The Kernel Logger needs **one** of:

- Membership in the local **Administrators** group, **or**
- The **`SeSystemProfilePrivilege`** ("Profile system performance") privilege.

The Scheduled-Task installer (`deploy/supervisors/windows/Install-Service.ps1`) runs the
server as the interactive user. That user is typically a **standard** user — no
Administrators, no `SeSystemProfilePrivilege` — so `collect_off_cpu_sample` returns a
structured `PermissionDenied` envelope:

```
{
  "error": {
    "kind": "PermissionDenied",
    "message": "NT Kernel Logger 'ContextSwitch' provider requires SeSystemProfilePrivilege or local Administrators membership.",
    "actionableHint": "See docs/windows-sidecar-service.md to run the sidecar as a Windows Service."
  }
}
```

Every other tool (`snapshot_counters`, `collect_cpu_sample`, `collect_exceptions`,
`collect_gc_events`, `collect_event_source`, ETW NativeAOT CPU sampling) works fine from
the Scheduled Task — they use EventPipe or user-mode ETW providers that do not need a
kernel logger session. **Switch to the Service only when you need off-CPU.**

> 🔑 **Same shape as the Linux side.** Linux requires the sidecar to (a) match the target
> app's UID for the diagnostic socket and (b) hold `CAP_PERFMON` for `perf record`. Windows
> needs (a) elevation **once** at install time and (b) `SeSystemProfilePrivilege` on the
> service account. Both are configure-once, then transparent for every subsequent tool
> call.

---

## 2. Two account options

| Account                         | Privilege grant                   | Tradeoff                                                                 |
|---------------------------------|-----------------------------------|--------------------------------------------------------------------------|
| `LocalSystem`                   | implicit (it has every privilege) | Simplest. **Overbroad** — `LocalSystem` can do everything on the box.    |
| Dedicated service account       | `SeSystemProfilePrivilege` only   | **Least privilege**. Recommended for hardened environments.              |

The recommendation is the dedicated account. The `LocalSystem` path is documented for
quick experimentation and air-gapped lab boxes.

---

## 3. Option A — dedicated service account (recommended)

### 3.1 Create the account

```powershell
# Run from an elevated PowerShell.
$pwd = Read-Host "Password for the service account" -AsSecureString
New-LocalUser -Name 'diagmcp$' -Password $pwd `
              -Description 'dotnet-diagnostics-mcp service account' `
              -PasswordNeverExpires `
              -UserMayNotChangePassword

# Grant "Log on as a service" (SeServiceLogonRight) — required for any non-system
# service identity.
$account = "$env:COMPUTERNAME\diagmcp$"
$tmp = New-TemporaryFile
secedit /export /cfg $tmp /areas USER_RIGHTS | Out-Null
(Get-Content $tmp) -replace 'SeServiceLogonRight = ', "SeServiceLogonRight = $account," |
    Set-Content $tmp
secedit /configure /db secedit.sdb /cfg $tmp /areas USER_RIGHTS | Out-Null
Remove-Item $tmp
```

> The trailing `$` on the account name is conventional for service-only accounts — it
> excludes it from `Get-LocalUser` shown to interactive users by default.

### 3.2 Grant `SeSystemProfilePrivilege`

Two equivalent paths.

**Scripted (recommended):**

```powershell
$account = "$env:COMPUTERNAME\diagmcp$"
$tmp = New-TemporaryFile
secedit /export /cfg $tmp /areas USER_RIGHTS | Out-Null
(Get-Content $tmp) -replace 'SeSystemProfilePrivilege = ', "SeSystemProfilePrivilege = $account," |
    Set-Content $tmp
secedit /configure /db secedit.sdb /cfg $tmp /areas USER_RIGHTS | Out-Null
Remove-Item $tmp

# Verify:
whoami /priv  # run AFTER the service starts under diagmcp$ — should list SeSystemProfilePrivilege
```

**Interactive (`secpol.msc`):**

1. `secpol.msc` → `Local Policies` → `User Rights Assignment` → `Profile system performance`.
2. Add the `diagmcp$` account.
3. OK. The grant takes effect after the next service start.

### 3.3 Install the service

```powershell
# 1. Install the global tool as the future service account so the shim lands in
#    the right %USERPROFILE%. Skip if you already published a self-contained binary
#    and point sc.exe at that instead.
$cred = Get-Credential -UserName "$env:COMPUTERNAME\diagmcp$" -Message "diagmcp$ password"
Start-Process powershell -Credential $cred -ArgumentList '-NoProfile','-Command',
    'dotnet tool install -g dotnet-diagnostics-mcp' -Wait

# 2. Register the Windows Service.
$exe = "C:\Users\diagmcp$\.dotnet\tools\dotnet-diagnostics-mcp.exe"
sc.exe create "dotnet-diagnostics-mcp" `
    binPath= "`"$exe`" --urls http://127.0.0.1:8787" `
    DisplayName= "dotnet-diagnostics-mcp" `
    start= auto `
    obj= "$env:COMPUTERNAME\diagmcp$" `
    password= "<diagmcp$ password>"

sc.exe description "dotnet-diagnostics-mcp" "MCP server for on-demand .NET performance diagnostics."
sc.exe failure     "dotnet-diagnostics-mcp" reset= 86400 actions= restart/30000/restart/60000/restart/120000
```

### 3.4 Configure the bearer token

The bearer token is read from the **`MCP_BEARER_TOKEN`** environment variable. Service
processes do not inherit user environment, so set it as a machine-scope variable that the
service account also sees:

```powershell
$token = -join ((1..32) | ForEach-Object { '{0:x2}' -f (Get-Random -Maximum 256) })
[Environment]::SetEnvironmentVariable('MCP_BEARER_TOKEN', $token, 'Machine')

# Save the token somewhere the MCP client can read it — it is NOT recoverable from the
# registry once you forget it. Common pattern: write to a DPAPI-protected file under
# %ProgramData%\dotnet-diagnostics-mcp\.
$tokenDir  = Join-Path $env:ProgramData 'dotnet-diagnostics-mcp'
New-Item -ItemType Directory -Force -Path $tokenDir | Out-Null
$bytes   = [Text.Encoding]::UTF8.GetBytes($token)
$crypted = [Security.Cryptography.ProtectedData]::Protect($bytes, $null, 'LocalMachine')
[IO.File]::WriteAllBytes((Join-Path $tokenDir 'bearer.dpapi'), $crypted)
```

> 🛈 If you prefer to keep the value out of the machine environment, use
> `SetEnvironmentVariable('MCP_BEARER_TOKEN', $token, 'User')` **under the
> `diagmcp$` profile**, or wrap the binary in a thin script that reads from a config file
> and re-exports the env var before invoking `dotnet-diagnostics-mcp.exe`. The server
> itself only looks at the process-environment variable.

### 3.5 Start + verify

```powershell
Start-Service dotnet-diagnostics-mcp
Get-Service   dotnet-diagnostics-mcp   # Status should be Running

# Confirm the privilege actually landed on the service token. Run from any
# elevated PowerShell:
$pid = (Get-Service dotnet-diagnostics-mcp).ServicesDependedOn  # cosmetic; PID via Get-CimInstance:
$svcPid = (Get-CimInstance Win32_Service -Filter "Name='dotnet-diagnostics-mcp'").ProcessId
(Get-Process -Id $svcPid).Threads | Out-Null     # touch the process to make sure it's alive
# Use Sysinternals `accesschk -p <pid>` or PowerShell's `whoami /priv` from a process
# launched under diagmcp$ to confirm SeSystemProfilePrivilege is listed.

# Health-check:
Invoke-WebRequest http://127.0.0.1:8787/health | Select-Object StatusCode
# Expected: 200
```

From an MCP client, `collect_off_cpu_sample` should now return a `BlockingStacksArtifact`
instead of `PermissionDenied`.

---

## 4. Option B — `LocalSystem`

Faster, but `LocalSystem` is the most privileged account on the box. Acceptable for
isolated diagnostics jump-boxes; **not** recommended for shared production hosts.

```powershell
$exe = "C:\Program Files\dotnet-diagnostics-mcp\dotnet-diagnostics-mcp.exe"  # path to your binary

sc.exe create "dotnet-diagnostics-mcp" `
    binPath= "`"$exe`" --urls http://127.0.0.1:8787" `
    DisplayName= "dotnet-diagnostics-mcp" `
    start= auto `
    obj= "LocalSystem"

sc.exe failure "dotnet-diagnostics-mcp" reset= 86400 actions= restart/30000/restart/60000/restart/120000

# Bearer token (same approach as 3.4):
$token = -join ((1..32) | ForEach-Object { '{0:x2}' -f (Get-Random -Maximum 256) })
[Environment]::SetEnvironmentVariable('MCP_BEARER_TOKEN', $token, 'Machine')

Start-Service dotnet-diagnostics-mcp
```

No privilege grant needed — `LocalSystem` carries `SeSystemProfilePrivilege` implicitly.

---

## 5. Uninstall

```powershell
Stop-Service     dotnet-diagnostics-mcp -ErrorAction SilentlyContinue
sc.exe delete    dotnet-diagnostics-mcp

# Optional: drop the privilege grant + account.
$account = "$env:COMPUTERNAME\diagmcp$"
$tmp = New-TemporaryFile
secedit /export /cfg $tmp /areas USER_RIGHTS | Out-Null
(Get-Content $tmp) -replace ",$([Regex]::Escape($account))", '' |
    Set-Content $tmp
secedit /configure /db secedit.sdb /cfg $tmp /areas USER_RIGHTS | Out-Null
Remove-Item $tmp

Remove-LocalUser -Name 'diagmcp$'
[Environment]::SetEnvironmentVariable('MCP_BEARER_TOKEN', $null, 'Machine')
```

---

## 6. Troubleshooting

| Symptom                                                                                          | Likely cause                                                                  |
|--------------------------------------------------------------------------------------------------|-------------------------------------------------------------------------------|
| Service fails to start with **`Error 1069: logon failure`**                                       | `SeServiceLogonRight` not granted to the service account (see § 3.1).         |
| `collect_off_cpu_sample` returns `PermissionDenied` despite running as a service                  | `SeSystemProfilePrivilege` not granted, **or** the service was started before the grant — restart the service after `secedit`. |
| `collect_off_cpu_sample` returns `Conflict: NT Kernel Logger session already exists`              | Another profiler (PerfView, WPR, `xperf`) is currently running. Stop it.       |
| `Authorization` header rejected with 401                                                          | The MCP client is reading a stale `MCP_BEARER_TOKEN` from User scope instead of Machine. Refresh the client env. |
| `whoami /priv` does **not** list `SeSystemProfilePrivilege` for the service process               | The privilege is on the **account**, but the service was started with a **filtered token** (rare; legacy 32-bit hosts). Verify the service is 64-bit; restart the host once. |

For Linux / containers, see [`consumer-install.md § 1.5`](./consumer-install.md#15-linux-enabling-clrmd-backed-tools-ptrace).
