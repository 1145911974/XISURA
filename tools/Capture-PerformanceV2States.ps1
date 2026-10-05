param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$captureScript = Join-Path $PSScriptRoot 'Capture-SecondaryCopyFromScreen.ps1'
$executable = Join-Path $repoRoot "src\Jiaolong.ControlCenter\bin\$Configuration\net10.0-windows10.0.26100.0\win-x64\Jiaolong.ControlCenter.exe"
$evidenceRoot = Join-Path $repoRoot 'docs\design\visual-reconstruction\performance'
$referenceRoot = Join-Path $repoRoot 'docs\design\sitich-handoff\performance'
$outputRoot = 'C:\Users\Administrator\AppData\Local\Temp\jiaolong-visual-qa\performance'
$node = 'C:\Program Files\nodejs\node.exe'
$compare = Join-Path $repoRoot 'tools\Visual-Reconstruction\compare.mjs'
$magick = Get-ChildItem -Path 'C:\Program Files\ImageMagick-*\magick.exe' -ErrorAction Stop |
    Select-Object -First 1 -ExpandProperty FullName

foreach ($path in @($captureScript, $executable, $node, $compare, $magick)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing visual QA dependency: $path" }
}

$states = @(
    @{ Id = 'default'; Route = 'PerformanceV2'; Reference = '01-performance-base-content.png' },
    @{ Id = 'preset-expanded'; Route = 'PerformanceV2Preset'; Reference = '02-performance-preset-expanded-content.png' },
    @{ Id = 'advanced-expanded'; Route = 'PerformanceV2Advanced'; Reference = '03-performance-advanced-content.png' }
)
$failed = @()

foreach ($state in $states) {
    Get-Process -Name 'Jiaolong.ControlCenter' -ErrorAction SilentlyContinue | Stop-Process -Force
    $stateDirectory = Join-Path $outputRoot $state.Id
    New-Item -ItemType Directory -Force -Path $stateDirectory | Out-Null
    $full = Join-Path $stateDirectory 'window.png'
    $actual = Join-Path $stateDirectory 'actual.png'
    $capture = & $captureScript -Executable $executable -Output $full -Arguments @("--accept-page=$($state.Route)", '--accept-secondary') -WindowWidth 1586 -WindowHeight 992 -UseApplicationPlacement
    if (-not $capture.IsSecondaryContained) { throw "State $($state.Id) was not captured on the secondary display." }

    & $magick $full -crop '1307x991+282+0' +repage -background black -gravity north -extent '1307x992' $actual
    if ($LASTEXITCODE -ne 0) { throw "ImageMagick failed for state $($state.Id)." }

    & $node $compare `
        --reference (Join-Path $referenceRoot $state.Reference) `
        --actual $actual `
        --mask (Join-Path $evidenceRoot "qa\reference-masks\$($state.Id).png") `
        --output $stateDirectory `
        --state $state.Id
    if ($LASTEXITCODE -ne 0) { $failed += $state.Id }
}

if ($failed.Count -gt 0) {
    throw "Visual parity not reached: $($failed -join ', '). Inspect overlay.png, diff.png and report.json."
}

Write-Output 'PASS performance visual parity: 3 states'
