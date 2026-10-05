param(
    [Parameter(Mandatory)]
    [ValidatePattern('^[a-z0-9-]+$')]
    [string]$Page
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$evidenceRoot = Join-Path $repoRoot "docs\design\visual-reconstruction\$Page"
$manifestPath = Join-Path $evidenceRoot 'layout.manifest.json'
$tokensPath = Join-Path $evidenceRoot 'design-tokens.json'
$statesPath = Join-Path $evidenceRoot 'states.json'

foreach ($path in @($manifestPath, $tokensPath, $statesPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Missing evidence file: $path"
    }
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$states = Get-Content -LiteralPath $statesPath -Raw | ConvertFrom-Json
$stateNames = @($states.PSObject.Properties.Name | Where-Object { $_ -ne 'schemaVersion' })
$ids = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)

foreach ($region in $manifest.regions) {
    if (-not $ids.Add([string]$region.id)) { throw "Duplicate region id: $($region.id)" }
    if ($region.renderKind -notin @('native', 'asset')) { throw "Unknown renderKind: $($region.renderKind)" }
    if ($region.bounds.x -lt 0 -or $region.bounds.y -lt 0 -or
        $region.bounds.width -le 0 -or $region.bounds.height -le 0 -or
        $region.bounds.x + $region.bounds.width -gt $manifest.canvas.width -or
        $region.bounds.y + $region.bounds.height -gt $manifest.canvas.height) {
        throw "Region is outside the canvas: $($region.id)"
    }
    foreach ($state in $region.states) {
        if ($state -notin $stateNames) { throw "Unknown state '$state' in region $($region.id)" }
    }
}

foreach ($stateName in $stateNames) {
    $state = $states.$stateName
    $referencePath = Join-Path $repoRoot "docs\design\sitich-handoff\performance\$($state.reference)"
    if (-not (Test-Path -LiteralPath $referencePath -PathType Leaf)) {
        throw "Missing approved reference: $referencePath"
    }
    foreach ($regionId in $state.visibleRegions) {
        if (-not $ids.Contains([string]$regionId)) { throw "Unknown visible region '$regionId' in state $stateName" }
    }
}

Write-Output "PASS $Page evidence: $($manifest.canvas.width)x$($manifest.canvas.height), $($stateNames.Count) states"
