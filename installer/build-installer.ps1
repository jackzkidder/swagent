<#
.SYNOPSIS
    Build the SwAgent MSI.

.DESCRIPTION
    Builds Release, regenerates the file list from that output, then compiles
    the MSI. Run in that order every time - a stale Files.wxs is how a
    dependency goes missing on a customer machine and not on yours.

    Needs the WiX .NET tool:
        dotnet tool install --global wix

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1
#>
[CmdletBinding()]
param(
    # Keep in step with <Version> in Directory.Build.props: that one stamps the
    # assembly the panel reports, this one names the MSI and drives the upgrade.
    [string]$Version = '0.1.1',
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot  = Split-Path -Parent $scriptDir
$binDir    = Join-Path $repoRoot 'src\SwAgent.AddIn\bin\Release\net48'
$outDir    = Join-Path $repoRoot 'artifacts'
$msiPath   = Join-Path $outDir "SwAgent-$Version-x64.msi"

# --- 1. build ------------------------------------------------------------
if (-not $SkipBuild) {
    Write-Host 'Building Release...' -ForegroundColor Cyan
    & dotnet build (Join-Path $repoRoot 'SwAgent.sln') -c Release -v quiet --nologo
    if ($LASTEXITCODE -ne 0) { Write-Error 'Build failed.'; exit 1 }
}

if (-not (Test-Path $binDir)) { Write-Error "Missing build output: $binDir"; exit 1 }

# --- 2. regenerate the file list ----------------------------------------
Write-Host 'Generating file list...' -ForegroundColor Cyan
& (Join-Path $scriptDir 'generate-files.ps1') -Configuration Release
$filesWxs = Join-Path $scriptDir 'Files.wxs'
if (-not (Test-Path $filesWxs)) { Write-Error 'File list generation produced nothing.'; exit 1 }

# --- 3. compile the MSI --------------------------------------------------
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }

$wix = Get-Command wix -ErrorAction SilentlyContinue
if (-not $wix) {
    $candidate = Join-Path $env:USERPROFILE '.dotnet\tools\wix.exe'
    if (Test-Path $candidate) { $wix = $candidate } else {
        Write-Error "The WiX tool was not found. Install it with:`n    dotnet tool install --global wix"
        exit 1
    }
}

Write-Host 'Compiling MSI...' -ForegroundColor Cyan

$wixArgs = @(
    'build',
    (Join-Path $scriptDir 'Package.wxs'),
    (Join-Path $scriptDir 'Files.wxs'),
    '-arch', 'x64',
    '-d', "BinDir=$binDir",
    '-d', "Version=$Version",
    '-ext', 'WixToolset.Util.wixext',
    '-o', $msiPath
)

& $wix @wixArgs
if ($LASTEXITCODE -ne 0) { Write-Error 'WiX build failed.'; exit 1 }

# --- 4. report -----------------------------------------------------------
$info = Get-Item $msiPath
Write-Host ''
Write-Host "Built $msiPath" -ForegroundColor Green
Write-Host ("  {0:N1} MB" -f ($info.Length / 1MB))
Write-Host ''
Write-Host 'NOT SIGNED. Unsigned installers trigger SmartScreen warnings that will'  -ForegroundColor Yellow
Write-Host 'cost trial conversions. Get an OV code-signing certificate before any'   -ForegroundColor Yellow
Write-Host 'external tester sees this, and sign both the MSI and SwAgent.AddIn.dll.' -ForegroundColor Yellow
Write-Host ''
Write-Host 'Install:   msiexec /i "' -NoNewline; Write-Host $msiPath -NoNewline; Write-Host '"'
Write-Host 'Uninstall: msiexec /x "' -NoNewline; Write-Host $msiPath -NoNewline; Write-Host '"'
Write-Host 'Logged:    msiexec /i "' -NoNewline; Write-Host $msiPath -NoNewline; Write-Host '" /l*v install.log'
