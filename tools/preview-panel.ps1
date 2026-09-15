<#
.SYNOPSIS
    Screenshot the task pane in each state, without SOLIDWORKS.

.DESCRIPTION
    Builds artifacts\panel-preview\preview.html from src\SwAgent.AddIn\Panel\panel.html
    with tools\panel-preview\mock-host.js injected as a fake WebView2 host, then
    renders every scenario with headless Edge.

    Nothing in the panel is exercised by the harness, so this is how a change
    to it gets looked at before it is installed. Open preview.html#done (etc.)
    in a browser to click around.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\preview-panel.ps1
    powershell -ExecutionPolicy Bypass -File tools\preview-panel.ps1 -Scenario done,error -Width 300 -Theme dark
#>
param(
    [string[]]$Scenario = @('setup', 'setup-error', 'empty', 'menu', 'working', 'done', 'stopped', 'error', 'batch'),
    [ValidateSet('light', 'dark', 'both')]
    [string]$Theme = 'both',
    [int]$Width = 340,
    [int]$Height = 760
)

$ErrorActionPreference = 'Stop'

# Under `powershell -File`, "-Scenario done,error" arrives as ONE string, not
# an array, and would silently render a single scenario named "done,error".
$Scenario = @($Scenario | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })

$repoRoot = Split-Path -Parent $PSScriptRoot
$panel    = Join-Path $repoRoot 'src\SwAgent.AddIn\Panel\panel.html'
$mock     = Join-Path $PSScriptRoot 'panel-preview\mock-host.js'
$outDir   = Join-Path $repoRoot 'artifacts\panel-preview'

$edge = @(
    "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe",
    "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $edge) { throw 'Microsoft Edge was not found; it is needed for headless screenshots.' }

New-Item -ItemType Directory -Force $outDir | Out-Null

$html = [IO.File]::ReadAllText($panel)
$at = $html.IndexOf('<script>')
if ($at -lt 0) { throw 'panel.html has no <script> block to inject the mock host before.' }

$inject  = "<script>`n" + [IO.File]::ReadAllText($mock) + "`n</script>`n"
$preview = Join-Path $outDir 'preview.html'
[IO.File]::WriteAllText($preview, $html.Substring(0, $at) + $inject + $html.Substring($at), (New-Object Text.UTF8Encoding $false))

# A throwaway profile, so this never collides with an Edge the user has open.
$profileDir = Join-Path $env:TEMP ('swagent-preview-' + [Guid]::NewGuid().ToString('N'))
$themes = if ($Theme -eq 'both') { @('light', 'dark') } else { @($Theme) }
$base = ([Uri]$preview).AbsoluteUri

# Headless Edge refuses windows narrower than about 500px. The window gets at
# least that; the mock host pins the page itself to -Width.
$windowWidth = [Math]::Max($Width, 520)

try {
    foreach ($t in $themes) {
        foreach ($s in $Scenario) {
            $png = Join-Path $outDir "$s-$t.png"
            if (Test-Path $png) { Remove-Item $png }

            $edgeArgs = @(
                '--headless=new', '--disable-gpu', '--hide-scrollbars', '--no-first-run',
                '--force-device-scale-factor=1', "--window-size=$windowWidth,$Height",
                '--virtual-time-budget=4000', "--user-data-dir=`"$profileDir`"",
                "--screenshot=`"$png`"", "`"$base`?theme=$t&w=$Width&still=1#$s`""
            )
            Start-Process -FilePath $edge -ArgumentList $edgeArgs -Wait -NoNewWindow

            if (Test-Path $png) { Write-Host "  $s ($t) -> $png" }
            else { Write-Warning "No screenshot for $s ($t)." }
        }
    }
}
finally {
    Remove-Item -Recurse -Force $profileDir -ErrorAction SilentlyContinue
}

Write-Host ''
Write-Host "Interactive: $base#done"
