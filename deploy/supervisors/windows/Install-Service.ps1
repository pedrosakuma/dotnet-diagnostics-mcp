<#
.SYNOPSIS
    Install dotnet-diagnostics-mcp as a per-user Scheduled Task on Windows.
.DESCRIPTION
    Registers a Scheduled Task that starts at user logon, auto-restarts on failure,
    and runs the dotnet global tool shim from %USERPROFILE%\.dotnet\tools.
    Requires the tool to be installed first:
        dotnet tool install -g dotnet-diagnostics-mcp
.PARAMETER Port
    TCP port to bind. Defaults to 8787.
.PARAMETER Token
    Bearer token published as the MCP_BEARER_TOKEN env var. Defaults to a freshly
    generated 64-char random hex string.
.EXAMPLE
    .\Install-Service.ps1 -Port 8787
#>
[CmdletBinding()]
param(
    [int]$Port = 8787,
    [string]$Token = $(([guid]::NewGuid().ToString('N') + [guid]::NewGuid().ToString('N')).Substring(0,64)),
    [string]$TaskName = 'dotnet-diagnostics-mcp'
)

$ErrorActionPreference = 'Stop'

$exe = Join-Path $env:USERPROFILE '.dotnet\tools\dotnet-diagnostics-mcp.exe'
if (-not (Test-Path $exe)) {
    Write-Error "dotnet-diagnostics-mcp.exe not found at $exe. Install first: dotnet tool install -g dotnet-diagnostics-mcp"
    exit 1
}

$urls = "http://127.0.0.1:$Port"
$action = New-ScheduledTaskAction -Execute $exe -Argument "--urls $urls"
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
$settings = New-ScheduledTaskSettingsSet `
    -StartWhenAvailable `
    -RestartCount 5 `
    -RestartInterval (New-TimeSpan -Seconds 30) `
    -ExecutionTimeLimit ([System.TimeSpan]::Zero) `
    -MultipleInstances IgnoreNew
$principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive

if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) {
    Write-Host "Removing existing task '$TaskName'..."
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
}

[Environment]::SetEnvironmentVariable('MCP_BEARER_TOKEN', $Token, 'User')

Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings -Principal $principal | Out-Null

Write-Host "Registered Scheduled Task '$TaskName'."
Write-Host "MCP_BEARER_TOKEN published to USER scope. Restart your shell to pick it up."
Write-Host "Endpoint: $urls/mcp"
Write-Host "Health probe: $exe --health-check --urls $urls"
Write-Host ""
Write-Host "Start now without waiting for logon: Start-ScheduledTask -TaskName '$TaskName'"
Write-Host "Uninstall:                            Unregister-ScheduledTask -TaskName '$TaskName' -Confirm:`$false"
