<#
.SYNOPSIS
    Register the SwAgent add-in with SOLIDWORKS. Requires elevation.

.DESCRIPTION
    Registration is two separate things and both are required:

      1. regasm /codebase  - makes the assembly a COM server, so Windows can
                             create the class.
      2. Registry keys     - SOLIDWORKS only loads add-ins listed under its own
                             Addins key. Without this, the COM server registers
                             perfectly and SOLIDWORKS never looks at it.

    Step 2 is performed by the [ComRegisterFunction] inside the assembly, which
    regasm invokes. This script runs regasm and then VERIFIES the keys exist,
    because a silent registration failure produces an add-in that installs
    "successfully" and never appears.

    This is the development equivalent of what the installer will do.

.PARAMETER Configuration
    Debug or Release. Defaults to Debug.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\register-addin.ps1
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'

$AddInGuid = '7DADCD66-C0C5-4ABB-A17D-5FDCDA0860A2'
$repoRoot  = Split-Path -Parent $PSScriptRoot
$assembly  = Join-Path $repoRoot "src\SwAgent.AddIn\bin\x64\$Configuration\net48\SwAgent.AddIn.dll"

# --- elevation ------------------------------------------------------------
$identity  = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error @"
This script must run elevated - it writes to HKLM.

Open an ADMINISTRATOR PowerShell and run:
  powershell -ExecutionPolicy Bypass -File "$PSCommandPath"
"@
    exit 1
}

# --- inputs ---------------------------------------------------------------
if (-not (Test-Path $assembly)) {
    Write-Error "Assembly not found: $assembly`nBuild it first: dotnet build"
    exit 1
}

# regasm must match the bitness of the add-in. This is an x64-only add-in, so
# the 64-bit regasm is the only correct one; the 32-bit one would register into
# the WOW6432Node redirect where SOLIDWORKS will not look.
$regasm = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe'
if (-not (Test-Path $regasm)) {
    Write-Error "64-bit RegAsm.exe not found at $regasm"
    exit 1
}

if (Get-Process SLDWORKS -ErrorAction SilentlyContinue) {
    Write-Warning 'SOLIDWORKS is running. Restart it after this completes, or the add-in will not appear.'
}

# --- register -------------------------------------------------------------
Write-Host "Registering $assembly" -ForegroundColor Cyan
Write-Host "  using $regasm"

# /codebase records the assembly's full path, which is what lets SOLIDWORKS
# load it from a build output folder rather than the GAC.
$output = & $regasm $assembly /codebase 2>&1
$output | ForEach-Object { Write-Host "  $_" }

if ($LASTEXITCODE -ne 0) {
    Write-Error "regasm failed with exit code $LASTEXITCODE"
    exit 1
}

# --- verify ---------------------------------------------------------------
# Do not trust the exit code alone: verify that SOLIDWORKS can actually see it.
Write-Host ''
Write-Host 'Verifying:' -ForegroundColor Cyan

$ok = $true

$addinKey = "HKLM:\SOFTWARE\SolidWorks\Addins\{$AddInGuid}"
if (Test-Path $addinKey) {
    $props = Get-ItemProperty $addinKey
    Write-Host "  [ok]   $addinKey" -ForegroundColor Green
    Write-Host "         Title = $($props.Title)"
} else {
    Write-Host "  [FAIL] $addinKey is missing - SOLIDWORKS will not see the add-in" -ForegroundColor Red
    $ok = $false
}

$startupKey = "HKCU:\Software\SolidWorks\AddInsStartup\{$AddInGuid}"
if (Test-Path $startupKey) {
    Write-Host "  [ok]   $startupKey" -ForegroundColor Green
} else {
    Write-Host "  [warn] $startupKey is missing - the add-in will not auto-load" -ForegroundColor Yellow
}

$clsidKey = "HKLM:\SOFTWARE\Classes\CLSID\{$AddInGuid}\InprocServer32"
if (Test-Path $clsidKey) {
    $codebase = (Get-ItemProperty $clsidKey).CodeBase
    Write-Host "  [ok]   COM server registered" -ForegroundColor Green
    Write-Host "         CodeBase = $codebase"
} else {
    Write-Host "  [FAIL] COM server not registered under CLSID" -ForegroundColor Red
    $ok = $false
}

Write-Host ''
if ($ok) {
    Write-Host 'Registered. Restart SOLIDWORKS, then look for "SwAgent" in the task pane.' -ForegroundColor Green
    Write-Host "Log: $env:LOCALAPPDATA\SwAgent\logs\swagent.log"
    exit 0
} else {
    Write-Error 'Registration did not complete correctly - see the failures above.'
    exit 1
}
