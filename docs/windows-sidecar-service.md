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

The Kernel Logger itself needs **one** of:

- Membership in the local **Administrators** group, **or**
- The **`SeSystemProfilePrivilege`** ("Profile system performance") privilege.

> ✅ **Current runtime gate.** `EtwOffCpuSampler.IsAvailable()` now accepts either
> local **Administrators** membership **or** a token that carries
> `SeSystemProfilePrivilege` (`Profile system performance`). That means a dedicated
> service account can now run `collect_off_cpu_sample` without joining
> **Administrators**, as long as you grant the privilege and restart the service.

The Scheduled-Task installer (`deploy/supervisors/windows/Install-Service.ps1`) runs the
server as the interactive user. That user is typically a **standard** user — no
Administrators, no `SeSystemProfilePrivilege` — so `collect_off_cpu_sample` returns a
structured `PermissionDenied` envelope:

```json
{
  "error": {
    "kind": "PermissionDenied",
    "message": "NT Kernel Logger 'ContextSwitch' provider requires either BUILTIN\\Administrators membership or SeSystemProfilePrivilege ('Profile system performance'). Grant one of those rights to the diagnostics sidecar account and restart the service."
  },
  "hints": [
    {
      "nextTool": "collect_off_cpu_sample",
      "reason": "Retry after the sidecar account has one of the two supported Windows paths: BUILTIN\\Administrators membership or SeSystemProfilePrivilege ('Profile system performance')."
    }
  ]
}
```

Every other tool (`snapshot_counters`, `collect_cpu_sample`, `collect_exceptions`,
`collect_gc_events`, `collect_activities`, `collect_event_source`, ETW NativeAOT CPU sampling) works fine from
the Scheduled Task — they use EventPipe or user-mode ETW providers that do not need a
kernel logger session. **Switch to the Service only when you need off-CPU.**

> 🔑 **Same shape as the Linux side.** Linux requires the sidecar to (a) match the target
> app's UID for the diagnostic socket and (b) hold `CAP_PERFMON` for `perf record`. Windows
> needs (a) elevation **once** at install time and (b) `SeSystemProfilePrivilege` on the
> service account. Both are configure-once, then transparent for every subsequent tool
> call.

---

## 2. Two account options

| Account                                       | Privilege grant                                                     | Tradeoff                                                                                                      |
|-----------------------------------------------|---------------------------------------------------------------------|---------------------------------------------------------------------------------------------------------------|
| Dedicated service account in **Administrators** | `Add-LocalGroupMember -Group Administrators`                        | Easiest path. Still broader than ideal — admin rights on the host.                                          |
| `LocalSystem`                                 | implicit (it has every privilege)                                   | Simplest. **Host-wide** privilege. Reasonable for isolated jump-boxes; not recommended for shared hosts.    |
| Dedicated service account with **`SeSystemProfilePrivilege` only** | grant `Profile system performance` via `secpol.msc` or `gpedit.msc` | **Least privilege** for off-CPU: enough for the NT Kernel Logger without putting the account in Administrators. |

Recommendation: **dedicated account with `SeSystemProfilePrivilege` only** for shared/production hosts,
**Administrators** when you need the quickest bootstrap, and **`LocalSystem`** only for isolated labs.

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
```

> The trailing `$` on the account name is conventional for service-only accounts — it
> excludes it from `Get-LocalUser` shown to interactive users by default. It is **not** a
> managed service account (`MSA`); plain `New-LocalUser` is fine for a single host.

### 3.2 Grant the required rights

```powershell
$account = "$env:COMPUTERNAME\diagmcp$"

# Fastest path: add to local Administrators. Idempotent — re-running is a no-op.
Add-LocalGroupMember -Group 'Administrators' -Member 'diagmcp$' -ErrorAction SilentlyContinue
```

> 🔒 **Least-privilege path (preferred for production).** Instead of adding the account
> to **Administrators**, grant only the two user rights the service needs:
>
> - `secpol.msc` **or** `gpedit.msc` → `Computer Configuration` → `Windows Settings` →
>   `Security Settings` → `Local Policies` → `User Rights Assignment` →
>   `Log on as a service` → Add `diagmcp$`.
> - Same node → `Profile system performance` (`SeSystemProfilePrivilege`) → Add `diagmcp$`.
>
> Both grants take effect on the next service start. After you add them, restart the
> service and `collect_off_cpu_sample` can use the NT Kernel Logger without local
> **Administrators** membership.

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
processes do not inherit the interactive user's environment, so it must be visible to the
service account at start time.

> ⚠️ **Security: avoid the Machine env scope for production.** A `Machine`-scope
> environment variable is readable by **every** local process on the host, so any local
> user could exfiltrate the token and call the privileged sidecar bound to
> `127.0.0.1:8787`. Likewise, a DPAPI blob protected under `LocalMachine` scope can be
> decrypted by any local account on the same box — it is **host-wide**, not
> service-account-isolated.

Recommended pattern for production: set the env var **under the `diagmcp$` profile only**,
or wrap the binary in a small launcher that reads from an ACL-restricted config file and
exports the variable before calling `dotnet-diagnostics-mcp.exe`.

```powershell
# Set MCP_BEARER_TOKEN under the diagmcp$ user profile (User scope) — visible only
# to processes running as diagmcp$.
$cred  = Get-Credential -UserName "$env:COMPUTERNAME\diagmcp$" -Message "diagmcp$ password"
$token = -join ((1..32) | ForEach-Object { '{0:x2}' -f (Get-Random -Maximum 256) })
Start-Process powershell -Credential $cred -ArgumentList '-NoProfile','-Command',
    "[Environment]::SetEnvironmentVariable('MCP_BEARER_TOKEN', '$token', 'User')" -Wait

# Persist the same token in an ACL-restricted file for the MCP client to read.
$tokenDir  = Join-Path $env:ProgramData 'dotnet-diagnostics-mcp'
New-Item -ItemType Directory -Force -Path $tokenDir | Out-Null
$tokenFile = Join-Path $tokenDir 'bearer.token'
Set-Content -Path $tokenFile -Value $token -Encoding ASCII

# ACL: only SYSTEM, Administrators, and the diagmcp$ account can read.
$acl = New-Object System.Security.AccessControl.FileSecurity
$acl.SetAccessRuleProtection($true, $false)   # disable inheritance
foreach ($principal in 'NT AUTHORITY\SYSTEM','BUILTIN\Administrators',"$env:COMPUTERNAME\diagmcp$") {
    $rule = New-Object System.Security.AccessControl.FileSystemAccessRule(
        $principal, 'Read,Write', 'Allow')
    $acl.AddAccessRule($rule)
}
Set-Acl -Path $tokenFile -AclObject $acl
```

> 🛈 Quick-and-dirty alternative (lab boxes only): `[Environment]::SetEnvironmentVariable(
> 'MCP_BEARER_TOKEN', $token, 'Machine')`. Acceptable for single-user hosts; **do not
> use** on shared production hosts — see the warning above.

### 3.5 Start + verify

```powershell
Start-Service dotnet-diagnostics-mcp
Get-Service   dotnet-diagnostics-mcp   # Status should be Running

# Confirm the service process is alive and grab its PID via WMI.
$svcPid = (Get-CimInstance Win32_Service -Filter "Name='dotnet-diagnostics-mcp'").ProcessId
Get-Process -Id $svcPid | Format-Table Id, ProcessName, StartTime

# Confirm the privileges actually landed on the service token. Use Sysinternals
# `accesschk.exe -p $svcPid` or open an interactive shell as diagmcp$ and run
# `whoami /priv` — SeSystemProfilePrivilege should be listed.

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

# Bearer token. Machine scope is acceptable here because LocalSystem already has
# unrestricted access to the host — every local user could read the token, but they
# could also DoS / inspect the service directly. For a shared host, prefer Option A
# with an ACL-restricted token file (see § 3.4) over LocalSystem.
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
# Remove-LocalGroupMember is idempotent and matches the install in § 3.2.
Remove-LocalGroupMember -Group 'Administrators' -Member 'diagmcp$' -ErrorAction SilentlyContinue

# If you ALSO granted SeSystemProfilePrivilege / SeServiceLogonRight via secpol.msc
# (future least-privilege path), remove them from the same UI:
#   secpol.msc -> Local Policies -> User Rights Assignment ->
#       "Log on as a service" / "Profile system performance" -> Remove diagmcp$.

Remove-LocalUser -Name 'diagmcp$'
[Environment]::SetEnvironmentVariable('MCP_BEARER_TOKEN', $null, 'Machine')
Remove-Item -Recurse -Force "$env:ProgramData\dotnet-diagnostics-mcp" -ErrorAction SilentlyContinue
```

---

## 6. Troubleshooting

| Symptom                                                                                          | Likely cause                                                                  |
|--------------------------------------------------------------------------------------------------|-------------------------------------------------------------------------------|
| Service fails to start with **`Error 1069: logon failure`**                                       | The service account cannot log on as a service. If you followed § 3.2, Administrators membership grants `SeServiceLogonRight` implicitly — confirm `Add-LocalGroupMember` actually ran (check `Get-LocalGroupMember Administrators`). |
| `collect_off_cpu_sample` returns `PermissionDenied` despite running as a service                  | Service started **before** the privilege grant, or the account has neither local **Administrators** membership nor `SeSystemProfilePrivilege`. Restart the service after changing group membership / User Rights Assignment (token privileges are evaluated at start). |
| `collect_off_cpu_sample` returns `Conflict: NT Kernel Logger session already exists`              | Another profiler (PerfView, WPR, `xperf`) is currently running. Stop it.       |
| `Authorization` header rejected with 401                                                          | The MCP client is reading a stale `MCP_BEARER_TOKEN` from User scope instead of the service account / token file. Refresh the client. |
| `whoami /priv` does **not** list `SeSystemProfilePrivilege` for the service process               | The privilege is on the **account**, but the service was started with a **filtered token** (rare; legacy 32-bit hosts). Verify the service is 64-bit; restart the host once. |

For Linux / containers, see [`consumer-install.md § 1.5`](./consumer-install.md#15-linux-enabling-clrmd-backed-tools-ptrace).
