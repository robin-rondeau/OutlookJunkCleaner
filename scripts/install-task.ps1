#requires -Version 5.1
#requires -RunAsAdministrator
<#
.SYNOPSIS
  Register the OutlookJunkCleaner Windows Scheduled Task: hourly between 06:00 and 23:00.
.DESCRIPTION
  Run this once (as administrator) on the always-on Windows machine after first-auth.ps1 succeeds.
  The task runs OutlookJunkAgent.exe with the project directory as the working directory.

  - Trigger: every hour starting 06:00, repeating for 17 hours (last fire 23:00).
  - Run as: the current user (DPAPI requires the same user that ran first-auth).
  - Log only when user is logged on.
  - Hard execution time limit: 10 minutes.

  Re-run this script with -Force to recreate the task with updated parameters.
#>

[CmdletBinding()]
param(
    [string] $TaskName = "OutlookJunkCleaner",
    [string] $ProjectDir = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path,
    [string] $AgentExe = "bin\OutlookJunkAgent.exe",
    [Parameter()][switch] $Force,
    [Parameter()][hashtable] $EnvVars
)

$ErrorActionPreference = "Stop"

$exePath = Join-Path $ProjectDir $AgentExe
if (-not (Test-Path $exePath)) {
    Write-Error "Agent executable not found: $exePath. Build and publish the project first (see README)."
    exit 1
}

if ($Force -and (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue)) {
    Write-Host "Removing existing task '$TaskName' (--Force)..." -ForegroundColor Yellow
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
}

# Build the action: run the agent exe in the project directory.
$action = New-ScheduledTaskAction -Execute $exePath -WorkingDirectory $ProjectDir

# Build the trigger: hourly between 06:00 and 23:00. Built by attaching a repetition pattern
# to a once-daily trigger, which Task Scheduler accepts and surfaces as "Daily; repeat every 1h for 17h".
$startAt = (Get-Date "06:00")
$base = New-ScheduledTaskTrigger -Daily -At $startAt
$rep = (New-ScheduledTaskTrigger -Once -At $startAt `
        -RepetitionInterval (New-TimeSpan -Hours 1) `
        -RepetitionDuration (New-TimeSpan -Hours 17)).Repetition
$base.Repetition = $rep

# Settings: don't pile up missed runs, hard timeout, allow on battery.
$settings = New-ScheduledTaskSettingsSet `
    -StartWhenAvailable `
    -DontStopIfGoingOnBatteries `
    -AllowStartIfOnBatteries `
    -MultipleInstances IgnoreNew `
    -ExecutionTimeLimit (New-TimeSpan -Minutes 10)

# Principal: run as the current user, only when logged on (DPAPI token cache is per-user).
$currentUser = "$env:USERDOMAIN\$env:USERNAME"
$principal = New-ScheduledTaskPrincipal -UserId $currentUser -LogonType Interactive -RunLevel Limited

$task = New-ScheduledTask -Action $action -Trigger $base -Settings $settings -Principal $principal

Register-ScheduledTask -TaskName $TaskName -InputObject $task -Force | Out-Null

# Optionally bake env vars into the task action via the registry environment? Easier alternative:
# instruct the user to set them user-wide. We just print a reminder.
Write-Host ""
Write-Host "Registered scheduled task '$TaskName'." -ForegroundColor Green
Write-Host "  Action: $exePath"
Write-Host "  Working dir: $ProjectDir"
Write-Host "  Trigger: daily, every hour from 06:00 to 23:00"
Write-Host "  Run as: $currentUser (only when logged on -- DPAPI requirement)"
Write-Host ""
Write-Host "Required user-scope environment variables (set with [Environment]::SetEnvironmentVariable(...,'User')):"
Write-Host "  OUTLOOK_JUNK_MCP_CLIENT_ID   Azure app registration ID"
Write-Host "  ANTHROPIC_API_KEY            (or whichever provider you configured)"
Write-Host ""
Write-Host "Phase B graduation:"
Write-Host "  [Environment]::SetEnvironmentVariable('OUTLOOK_JUNK_MCP_ALLOW_DELETE','1','User')"
Write-Host "  ...then update rubric.md to tell the agent it now has delete authority."
Write-Host ""
Write-Host "Force a test run now: Start-ScheduledTask -TaskName '$TaskName'"
