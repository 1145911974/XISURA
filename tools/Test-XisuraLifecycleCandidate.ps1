[CmdletBinding()]
param([string]$BuildManifest, [string]$OriginalBoundary, [string]$OutputPath)

$script:Checks = New-Object 'System.Collections.Generic.List[object]'
$script:CandidatePaths = New-Object 'System.Collections.Generic.List[string]'
$script:CandidateProcesses = New-Object 'System.Collections.Generic.List[object]'
$script:ProductsPassed = $false
$script:ConfigsPassed = $false
$script:RecoveryAuthorityMatches = $false
$script:Journal = $null
$script:Service = $null

function Add-Check([string]$Name, [bool]$Passed, [string]$Detail) {
    [void]$script:Checks.Add([pscustomobject]@{ Name = $Name; Passed = $Passed; Detail = $Detail })
}

function Assert-SafePath([string]$Path, [string]$Kind = 'Leaf') {
    if ([string]::IsNullOrWhiteSpace($Path) -or -not [IO.Path]::IsPathFullyQualified($Path)) { throw 'Path must be fully qualified.' }
    $full = [IO.Path]::GetFullPath($Path)
    if ([IO.Path]::GetPathRoot($full) -ine 'C:\') { throw 'Path must be on C:\.' }
    if ($full.Substring(2).Contains(':')) { throw 'Alternate data streams are rejected.' }
    $current = 'C:\'
    $rootItem = Get-Item -LiteralPath $current -Force -ErrorAction Stop
    if (($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'C:\ is a reparse point.' }
    foreach ($part in $full.Substring(3).Split([char]'\', [StringSplitOptions]::RemoveEmptyEntries)) {
        $current = Join-Path $current $part
        if (-not (Test-Path -LiteralPath $current)) { throw "Missing path: $current" }
        $item = Get-Item -LiteralPath $current -Force -ErrorAction Stop
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Reparse path rejected: $current" }
    }
    if (-not (Test-Path -LiteralPath $full -PathType $Kind)) { throw "Expected $Kind path: $full" }
    return $full
}

function Assert-SafeOutput([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path) -or -not [IO.Path]::IsPathFullyQualified($Path)) { throw 'OutputPath must be fully qualified.' }
    $full = [IO.Path]::GetFullPath($Path)
    if ([IO.Path]::GetPathRoot($full) -ine 'C:\') { throw 'OutputPath must be on C:\.' }
    if ($full.Substring(2).Contains(':')) { throw 'Alternate data streams are rejected.' }
    [void](Assert-SafePath ([IO.Path]::GetDirectoryName($full)) 'Container')
    if ([IO.Path]::GetFileName($full) -eq '' -or (Test-Path -LiteralPath $full)) { throw 'Output exists or has no file name; CreateNew will not overwrite.' }
    return $full
}

function Get-Field($Object, [string]$Name, [string]$Kind = 'String') {
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { throw "Missing field '$Name'." }
    $value = $property.Value
    if ($Kind -eq 'Array') {
        if ($value -isnot [array] -or $value.Count -eq 0) { throw "Field '$Name' must be a nonempty array." }
    } elseif ($value -isnot [string] -or [string]::IsNullOrWhiteSpace($value)) { throw "Field '$Name' must be a nonempty string." }
    return $value
}

function Read-JsonObject([string]$Path) {
    $value = [IO.File]::ReadAllText($Path, [Text.Encoding]::UTF8) | ConvertFrom-Json -ErrorAction Stop
    if ($null -eq $value -or $value -isnot [pscustomobject]) { throw 'JSON root must be an object.' }
    return $value
}

function Test-HashEntries($Entries, [string]$Label) {
    $passed = $true; $index = 0
    foreach ($entry in $Entries) {
        $index++
        try {
            if ($entry -isnot [pscustomobject]) { throw 'Entry must be an object.' }
            $path = Get-Field $entry 'Path'; $expected = Get-Field $entry 'SHA256'
            if ($expected -notmatch '^[0-9A-Fa-f]{64}$') { throw 'SHA256 must contain 64 hexadecimal characters.' }
            $path = Assert-SafePath $path 'Leaf'
            if ($Label -eq 'Product') { [void]$script:CandidatePaths.Add($path) }
            $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256 -ErrorAction Stop).Hash
            $match = $actual -ieq $expected
            Add-Check "$Label[$index]" $match $(if ($match) { 'SHA256 matches.' } else { "SHA256 mismatch; actual $actual." })
            if (-not $match) { $passed = $false }
        } catch { $passed = $false; Add-Check "$Label[$index]" $false $_.Exception.Message }
    }
    return $passed
}

if ([string]::IsNullOrWhiteSpace($BuildManifest)) { Add-Check 'Build manifest' $false 'BuildManifest is required.' }
else {
    try {
        $manifest = Read-JsonObject (Assert-SafePath $BuildManifest 'Leaf')
        $products = Get-Field $manifest 'Products' 'Array'
        $script:ProductsPassed = Test-HashEntries $products 'Product'
        Add-Check 'Candidate product hashes' $script:ProductsPassed "Checked $($products.Count) product(s)."
    } catch { Add-Check 'Build manifest' $false $_.Exception.Message }
}

if ([string]::IsNullOrWhiteSpace($OriginalBoundary)) { Add-Check 'Original boundary' $false 'OriginalBoundary is required.' }
else {
    try {
        $boundary = Read-JsonObject (Assert-SafePath $OriginalBoundary 'Leaf')
        $configs = Get-Field $boundary 'Configs' 'Array'
        $script:ConfigsPassed = Test-HashEntries $configs 'Config'
        Add-Check 'Original config hashes' $script:ConfigsPassed "Checked $($configs.Count) config(s)."
        $journalText = Get-Field $boundary 'Journal'
        $journalExpected = Get-Field $boundary 'JournalHash'
        if ($journalExpected -notmatch '^[0-9A-Fa-f]{64}$') { throw 'JournalHash must contain 64 hexadecimal characters.' }
        $journalPath = Assert-SafePath $journalText 'Leaf'
        $pathBound = $journalPath -ieq [IO.Path]::GetFullPath($journalText)
        $journalActual = (Get-FileHash -LiteralPath $journalPath -Algorithm SHA256 -ErrorAction Stop).Hash
        $script:RecoveryAuthorityMatches = $pathBound -and ($journalActual -ieq $journalExpected)
        $script:Journal = [pscustomobject]@{ Path = $journalPath; OriginalDirectory = [IO.Path]::GetDirectoryName($journalPath); ExpectedSHA256 = $journalExpected; ActualSHA256 = $journalActual; PathBoundToBoundary = $pathBound }
        Add-Check 'Original journal path and hash' $script:RecoveryAuthorityMatches 'Uses the exact absolute Journal path recorded by OriginalBoundary; no journal was created or copied.'
    } catch { $script:RecoveryAuthorityMatches = $false; Add-Check 'Original boundary or journal' $false $_.Exception.Message }
}

try {
    $service = @(Get-CimInstance -ClassName Win32_Service -Filter "Name='JiaolongControlService'" -ErrorAction Stop)
    $ok = $service.Count -eq 1 -and $service[0].State -eq 'Stopped' -and $service[0].StartMode -eq 'Disabled'
    if ($service.Count -eq 1) { $script:Service = [pscustomobject]@{ Name = $service[0].Name; State = $service[0].State; StartMode = $service[0].StartMode } }
    Add-Check 'Installed 1.0.109 service' $ok 'Read-only Win32_Service query; expected Stopped and Disabled.'
} catch { Add-Check 'Installed 1.0.109 service' $false $_.Exception.Message }

try {
    $names = @('JiaolongControlCenter.exe', 'Jiaolong.ControlCenter.exe', 'Jiaolong.Service.exe', 'Jiaolong.ReadOnlyHost.exe', 'Xisura.ReadOnlyHost.exe', 'ManualHost.exe') + @($script:CandidatePaths | ForEach-Object { [IO.Path]::GetFileNameWithoutExtension($_) + '.exe' })
    $directories = @($script:CandidatePaths | ForEach-Object { [IO.Path]::GetDirectoryName($_) } | Select-Object -Unique)
    $processes = @(Get-CimInstance -ClassName Win32_Process -ErrorAction Stop)
    foreach ($process in $processes) {
        $matched = $names -contains $process.Name
        if (-not $matched -and $process.ExecutablePath) {
            foreach ($directory in $directories) {
                if ($process.ExecutablePath.StartsWith($directory.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { $matched = $true; break }
            }
        }
        if (-not $matched -and $process.Name -in @('dotnet.exe', 'testhost.exe') -and $process.CommandLine) {
            foreach ($path in $script:CandidatePaths) {
                if ($process.CommandLine.IndexOf($path, [StringComparison]::OrdinalIgnoreCase) -ge 0) { $matched = $true; break }
            }
        }
        if ($matched) { [void]$script:CandidateProcesses.Add([pscustomobject]@{ ProcessId = $process.ProcessId; Name = $process.Name; ExecutablePath = $process.ExecutablePath }) }
    }
    Add-Check 'Candidate processes absent' ($script:CandidateProcesses.Count -eq 0) "Read-only Win32_Process query found $($script:CandidateProcesses.Count) related process(es)."
} catch { Add-Check 'Candidate process query' $false $_.Exception.Message }

$evidencePath = $null
if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
    try { $evidencePath = Assert-SafeOutput $OutputPath; Add-Check 'Evidence output path' $true 'Existing safe C:\ parent; output will use CreateNew.' }
    catch { Add-Check 'Evidence output path' $false $_.Exception.Message }
}

$artifactPassed = $script:ProductsPassed -and $script:ConfigsPassed
$readOnlyPassed = @($script:Checks | Where-Object { -not $_.Passed }).Count -eq 0
$result = [pscustomobject]@{
    GeneratedAtUtc = [DateTime]::UtcNow.ToString('o')
    BuildManifest = $BuildManifest
    OriginalBoundary = $OriginalBoundary
    ArtifactChecksPassed = $artifactPassed
    RecoveryAuthorityMatches = $script:RecoveryAuthorityMatches
    HardwareWriteAuthorized = $false
    ReadOnlyPreflightChecksPassed = $readOnlyPassed
    Scope = 'Read-only artifact, journal, service, and process preflight. No launch, hardware validation, or hardware-write authorization.'
    Journal = $script:Journal
    Service = $script:Service
    CandidateProcesses = @($script:CandidateProcesses.ToArray())
    Checks = @($script:Checks.ToArray())
}
$json = $result | ConvertTo-Json -Depth 8
if ($evidencePath) {
    try {
        $stream = [IO.File]::Open($evidencePath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $bytes = [System.Text.UTF8Encoding]::new($false).GetBytes($json + [Environment]::NewLine); $stream.Write($bytes, 0, $bytes.Length) }
        finally { $stream.Dispose() }
    } catch {
        Add-Check 'Evidence file creation' $false $_.Exception.Message
        $result.ReadOnlyPreflightChecksPassed = $false
        $result.Checks = @($script:Checks.ToArray())
        $json = $result | ConvertTo-Json -Depth 8
    }
}
Write-Output $json
if ($result.ReadOnlyPreflightChecksPassed) { exit 0 }
exit 1