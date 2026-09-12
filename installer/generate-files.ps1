<#
.SYNOPSIS
    Generate Files.wxs from the Release build output.

.DESCRIPTION
    The add-in ships two dozen assemblies, most of them dragged in by the
    Anthropic SDK. Hand-maintaining that list in the installer guarantees it
    will drift the first time a dependency is added, and the symptom is a
    missing-assembly failure on a customer machine rather than on ours.

    So the list is generated. Run this after building Release and before
    building the MSI; build-installer.ps1 does both in order.

    .pdb files are deliberately excluded - they are debug symbols, not
    something to ship.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot  = Split-Path -Parent $scriptDir
$binDir    = Join-Path $repoRoot "src\SwAgent.AddIn\bin\$Configuration\net48"

if (-not $OutputPath) { $OutputPath = Join-Path $scriptDir 'Files.wxs' }

if (-not (Test-Path $binDir)) {
    Write-Error "Build output not found: $binDir`nBuild first: dotnet build -c $Configuration"
    exit 1
}

# Everything that is not a debug symbol. The runtimes\ folder is skipped
# because WebView2Loader.dll is already copied to the output root, and shipping
# both would put two copies of the same native library in Program Files.
$files = Get-ChildItem -Path $binDir -File |
    Where-Object { $_.Extension -notin @('.pdb') } |
    Sort-Object Name

$assets = @()
$assetDir = Join-Path $binDir 'Assets'
if (Test-Path $assetDir) {
    $assets = Get-ChildItem -Path $assetDir -File | Sort-Object Name
}

if ($files.Count -eq 0) { Write-Error "No files found in $binDir"; exit 1 }

$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine('<?xml version="1.0" encoding="utf-8"?>')
[void]$sb.AppendLine('<!--')
[void]$sb.AppendLine('    GENERATED FILE - do not edit by hand.')
[void]$sb.AppendLine('    Produced by installer/generate-files.ps1 from the Release build output.')
[void]$sb.AppendLine("    Regenerate after adding or removing a dependency.")
[void]$sb.AppendLine('-->')
[void]$sb.AppendLine('<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">')
[void]$sb.AppendLine('  <Fragment>')
[void]$sb.AppendLine('    <ComponentGroup Id="AddInFiles" Directory="INSTALLFOLDER">')

# The add-in assembly is the COM server and carries the registry entries, so it
# is authored by hand in Package.wxs rather than generated here.
$comServer = 'SwAgent.AddIn.dll'

foreach ($f in $files) {
    if ($f.Name -eq $comServer) { continue }

    $id = 'file_' + ($f.Name -replace '[^A-Za-z0-9_]', '_')
    [void]$sb.AppendLine("      <Component Id=""$id"">")
    [void]$sb.AppendLine("        <File Id=""$id`_f"" Source=""`$(BinDir)\$($f.Name)"" KeyPath=""yes"" />")
    [void]$sb.AppendLine('      </Component>')
}

[void]$sb.AppendLine('    </ComponentGroup>')

if ($assets.Count -gt 0) {
    [void]$sb.AppendLine('')
    [void]$sb.AppendLine('    <ComponentGroup Id="AddInAssets" Directory="ASSETSFOLDER">')
    foreach ($a in $assets) {
        $id = 'asset_' + ($a.Name -replace '[^A-Za-z0-9_]', '_')
        [void]$sb.AppendLine("      <Component Id=""$id"">")
        [void]$sb.AppendLine("        <File Id=""$id`_f"" Source=""`$(BinDir)\Assets\$($a.Name)"" KeyPath=""yes"" />")
        [void]$sb.AppendLine('      </Component>')
    }
    [void]$sb.AppendLine('    </ComponentGroup>')
}

[void]$sb.AppendLine('  </Fragment>')
[void]$sb.AppendLine('</Wix>')

Set-Content -Path $OutputPath -Value $sb.ToString() -Encoding UTF8

$shipped = $files.Count - 1   # the COM server is authored separately
Write-Host "Wrote $OutputPath" -ForegroundColor Green
Write-Host "  $shipped dependency file(s) + $($assets.Count) asset(s), plus $comServer authored in Package.wxs"
