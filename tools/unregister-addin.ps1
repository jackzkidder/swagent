<#
.SYNOPSIS
    Unregister the SwAgent add-in. Requires elevation.

.DESCRIPTION
    Uninstall must fully unregister. A stale add-in entry pointing at a deleted
    DLL throws an error on EVERY SOLIDWORKS launch, forever, and generates
    support tickets from people who no longer use the product.

    So this script does not stop at regasm /unregister. It verifies that every
    key is gone afterwards and removes any that regasm left behind - which is
    the normal case when the assembly file has already been deleted, because
    regasm cannot run the [ComUnregisterFunction] of an assembly it cannot load.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\unregister-addin.ps1
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Continue'

$AddInGuid = '7DADCD66-C0C5-4ABB-A17D-5FDCDA0860A2'
$repoRoot  = Split-Path -Parent $PSScriptRoot
$assembly  = Join-Path $repoRoot "src\SwAgent.AddIn\bin\x64\$Configuration\net48\SwAgent.AddIn.dll"

$identity  = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error "This script must run elevated. Open an ADMINISTRATOR PowerShell and run:`n  powershell -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    exit 1
}

# --- the polite path ------------------------------------------------------
$regasm = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe'
if ((Test-Path $regasm) -and (Test-Path $assembly)) {
    Write-Host 'Running regasm /unregister' -ForegroundColor Cyan
    & $regasm $assembly /unregister 2>&1 | ForEach-Object { Write-Host "  $_" }
} else {
    Write-Warning 'Assembly or regasm not available; cleaning the registry directly.'
}

# --- the thorough path ----------------------------------------------------
# Remove anything left over regardless of how regasm got on.
Write-Host ''
Write-Host 'Removing any leftover keys:' -ForegroundColor Cyan

$keys = @(
    "HKLM:\SOFTWARE\SolidWorks\Addins\{$AddInGuid}",
    "HKCU:\Software\SolidWorks\AddInsStartup\{$AddInGuid}",
    "HKLM:\SOFTWARE\Classes\CLSID\{$AddInGuid}"
)

foreach ($key in $keys) {
    if (Test-Path $key) {
        try {
            Remove-Item -Path $key -Recurse -Force -ErrorAction Stop
            Write-Host "  removed  $key" -ForegroundColor Green
        } catch {
            Write-Host "  FAILED   $key : $_" -ForegroundColor Red
        }
    } else {
        Write-Host "  absent   $key"
    }
}

# --- verify ---------------------------------------------------------------
Write-Host ''
$remaining = $keys | Where-Object { Test-Path $_ }

if ($remaining) {
    Write-Host 'Orphaned keys remain:' -ForegroundColor Red
    $remaining | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    exit 1
}

Write-Host 'Fully unregistered. No orphaned keys.' -ForegroundColor Green
exit 0
