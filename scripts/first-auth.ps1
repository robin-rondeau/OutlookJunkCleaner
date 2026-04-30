#requires -Version 5.1
<#
.SYNOPSIS
  Run the MCP server in --first-auth mode to perform device-code sign-in and warm the MSAL token cache.
.DESCRIPTION
  Run this once on the always-on Windows machine before installing the scheduled task.
  The token cache is encrypted with DPAPI under the user account that runs this script,
  so the scheduled task must run as the same user.
#>

[CmdletBinding()]
param(
    [string] $ServerPath = (Join-Path $PSScriptRoot "..\bin\OutlookJunkMcp.exe")
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $ServerPath)) {
    Write-Error "Server executable not found: $ServerPath. Build and publish the project first (see README)."
    exit 1
}

if (-not $env:OUTLOOK_JUNK_MCP_CLIENT_ID) {
    Write-Error "Environment variable OUTLOOK_JUNK_MCP_CLIENT_ID is not set. Set it to your Azure app registration's client ID and re-run."
    exit 1
}

Write-Host "Starting device-code sign-in..." -ForegroundColor Cyan
Write-Host "When the device-code message appears, visit the URL it prints in any browser, paste the code, and sign in to your Outlook consumer account."
Write-Host ""

& $ServerPath --first-auth

if ($LASTEXITCODE -ne 0) {
    Write-Error "First-auth failed (exit $LASTEXITCODE)."
    exit $LASTEXITCODE
}

Write-Host ""
Write-Host "First-auth complete. Token cache is warm." -ForegroundColor Green
Write-Host "Verify by running: $ServerPath --self-test"
