param()

$ErrorActionPreference = 'Stop'
$exportDirectory = 'C:\ProgramData\JiaolongControlCenter\Diagnostics\Exports'
$expectedEntries = @('capabilities.json', 'checksums.sha256', 'fingerprint.json',
    'logs.ndjson', 'service-health.json', 'telemetry-60s.json', 'versions.json')
$expectedPath = [IO.Path]::GetFullPath($exportDirectory)
$tempDirectory = 'C:\Users\Administrator\AppData\Local\Temp'
$userSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$stagedPath = $null
$copyPath = $null
$stageDeleted = $false

function Read-Frame($pipe) {
    function Read-Bytes([int]$length) {
        $buffer = [byte[]]::new($length)
        $offset = 0
        while ($offset -lt $length) {
            $read = $pipe.ReadAsync($buffer, $offset, $length - $offset).WaitAsync([TimeSpan]::FromSeconds(60)).GetAwaiter().GetResult()
            if ($read -eq 0) { throw 'Service connection closed' }
            $offset += $read
        }
        return ,$buffer
    }
    $length = [BitConverter]::ToInt32((Read-Bytes 4), 0)
    if ($length -le 0 -or $length -gt 1048576) { throw 'Invalid service frame length' }
    [Text.Encoding]::UTF8.GetString((Read-Bytes $length)) | ConvertFrom-Json
}

function Send-Frame($pipe, $value) {
    $bytes = [Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -InputObject $value -Depth 12 -Compress))
    $size = [BitConverter]::GetBytes([int]$bytes.Length)
    $pipe.Write($size, 0, 4)
    $pipe.Write($bytes, 0, $bytes.Length)
    $pipe.Flush()
}

function Invoke-Service([string]$operation, [Guid]$operationId, $payload) {
    $pipe = [IO.Pipes.NamedPipeClientStream]::new('.', 'Jiaolong.ControlCenter.v1',
        [IO.Pipes.PipeDirection]::InOut, [IO.Pipes.PipeOptions]::Asynchronous)
    try {
        $pipe.Connect(3000)
        $version = [ordered]@{ major = 1; minor = 0 }
        Send-Frame $pipe ([ordered]@{ kind = 'hello'; protocolVersion = $version;
            messageId = [Guid]::NewGuid(); sentAtUtc = [DateTimeOffset]::UtcNow;
            clientVersion = 'diagnostic-export-acceptance' })
        if ((Read-Frame $pipe).kind -ne 'helloAck') { throw 'Service handshake failed' }
        Send-Frame $pipe ([ordered]@{ kind = 'request'; protocolVersion = $version;
            messageId = [Guid]::NewGuid(); sentAtUtc = [DateTimeOffset]::UtcNow;
            operationId = $operationId; operation = $operation;
            deadlineUtc = [DateTimeOffset]::UtcNow.AddSeconds(60); payload = $payload })
        $response = Read-Frame $pipe
        if ($response.status -ne 'success') { throw "Service $operation failed: $($response.error.code)" }
        return $response.payload
    }
    finally { $pipe.Dispose() }
}

if (Get-Process -Name '007FirstLight' -ErrorAction SilentlyContinue) {
    throw 'Diagnostic export refused: game test is paused'
}
if ((Get-Service JiaolongControlService).Status -ne 'Running') { throw 'Service is not running' }
if (-not (Test-Path -LiteralPath $tempDirectory -PathType Container)) { throw 'C: user Temp is unavailable' }
if (Test-Path -LiteralPath $expectedPath -PathType Container) {
    if (([IO.File]::GetAttributes($expectedPath) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'Export directory is a reparse point'
    }
}

try {
    $requestId = [Guid]::NewGuid()
    $export = Invoke-Service 'exportDiagnostics' $requestId ([ordered]@{ operationId = $requestId })
    $stagedPath = [IO.Path]::GetFullPath([string]$export.filePath)
    $parsedId = [Guid]::Empty
    if ([IO.Path]::GetDirectoryName($stagedPath) -ne $expectedPath -or
        -not [Guid]::TryParse([IO.Path]::GetFileNameWithoutExtension($stagedPath), [ref]$parsedId) -or
        [IO.Path]::GetExtension($stagedPath) -ne '.zip') { throw 'Unexpected staged export path' }
    if (-not (Test-Path -LiteralPath $stagedPath -PathType Leaf)) { throw 'Staged export missing' }
    $exportId = [Guid]::Parse([IO.Path]::GetFileNameWithoutExtension($stagedPath))

    $acl = Get-Acl -LiteralPath $stagedPath
    $allowedSids = @($acl.GetAccessRules($true, $false, [Security.Principal.SecurityIdentifier]) |
        Where-Object AccessControlType -eq Allow | ForEach-Object { $_.IdentityReference.Value })
    $expectedSids = @('S-1-5-18', 'S-1-5-32-544', $userSid)
    if (-not $acl.AreAccessRulesProtected -or
        @($allowedSids | Where-Object { $_ -notin $expectedSids }).Count -ne 0 -or
        @($expectedSids | Where-Object { $_ -notin $allowedSids }).Count -ne 0) {
        throw 'Staged export ACL differs from service contract'
    }

    $copyPath = Join-Path $tempDirectory ("diagnostic-acceptance-$([Guid]::NewGuid().ToString('N')).zip")
    [IO.File]::Copy($stagedPath, $copyPath, $false)
    $stagedHash = (Get-FileHash -LiteralPath $stagedPath -Algorithm SHA256).Hash
    $copiedHash = (Get-FileHash -LiteralPath $copyPath -Algorithm SHA256).Hash
    if ($stagedHash -ne $copiedHash) { throw 'Copied archive hash differs' }

    $archive = [IO.Compression.ZipFile]::OpenRead($copyPath)
    try {
        $entryNames = @($archive.Entries | ForEach-Object FullName | Sort-Object)
        if (($entryNames -join '|') -ne (($expectedEntries | Sort-Object) -join '|')) {
            throw 'Diagnostic archive entries incomplete'
        }
        $content = @{}
        foreach ($entry in $archive.Entries) {
            $stream = $entry.Open()
            try {
                $memory = [IO.MemoryStream]::new()
                try { $stream.CopyTo($memory); $content[$entry.FullName] = $memory.ToArray() }
                finally { $memory.Dispose() }
            }
            finally { $stream.Dispose() }
        }
        $checksumLines = @([Text.Encoding]::UTF8.GetString($content['checksums.sha256']).Split("`n") |
            Where-Object { $_.Trim() })
        foreach ($line in $checksumLines) {
            if ($line -notmatch '^([0-9a-f]{64})  ([^\r\n]+)\r?$') { throw 'Malformed archive checksum' }
            $name = $Matches[2]
            if (-not $content.ContainsKey($name) -or
                [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($content[$name])).ToLowerInvariant() -ne $Matches[1]) {
                throw 'Archive checksum mismatch'
            }
        }
        if ($checksumLines.Count -ne $expectedEntries.Count - 1) { throw 'Archive checksum count mismatch' }
        $identifierLeaks = @($content.Keys | Where-Object { $_ -ne 'checksums.sha256' } | Where-Object {
            $body = [Text.Encoding]::UTF8.GetString($content[$_])
            $body.Contains($userSid, [StringComparison]::OrdinalIgnoreCase) -or
            $body -match "(?i)\b$([regex]::Escape([Environment]::UserName))\b" -or
            $body -match '(?i)[A-Z]:\\{1,2}Users\\{1,2}'
        } | Sort-Object)
        foreach ($name in @('versions.json', 'fingerprint.json', 'capabilities.json')) {
            if ($content[$name].Length -eq 0) { throw "Diagnostic entry is empty: $name" }
            [Text.Encoding]::UTF8.GetString($content[$name]) | ConvertFrom-Json | Out-Null
        }
        $health = [Text.Encoding]::UTF8.GetString($content['service-health.json']) | ConvertFrom-Json
        $telemetry = [Text.Encoding]::UTF8.GetString($content['telemetry-60s.json']) | ConvertFrom-Json
        $coverage = $health.telemetryCoverage
        if ($null -eq $health.controlPipeline -or $null -eq $coverage -or $coverage.requestedSeconds -ne 60 -or
            $coverage.sampleCount -ne @($telemetry).Count) { throw 'Telemetry coverage metadata mismatch' }
        $logLineCount = @([Text.Encoding]::UTF8.GetString($content['logs.ndjson']).Split("`n") |
            Where-Object { $_.Trim() }).Count
        $sampleCount = @($telemetry).Count
        $actualSeconds = $coverage.actualSeconds
        $maximumGapMs = $coverage.maximumGapMs
        $gapsOverTwoSeconds = $coverage.gapsOverTwoSeconds
    }
    finally { $archive.Dispose() }

    $deleted = Invoke-Service 'deleteDiagnosticExport' $exportId ([ordered]@{ exportId = $exportId })
    if ($deleted.deleted -ne $true -or (Test-Path -LiteralPath $stagedPath)) { throw 'Service staging cleanup unconfirmed' }
    $stageDeleted = $true
    [pscustomobject]@{ installedVersion = (Get-Item 'C:\Program Files\Jiaolong Control Center\Jiaolong.Service.exe').VersionInfo.ProductVersion;
        entryCount = $entryNames.Count; checksumsVerified = $checksumLines.Count; copyHashMatched = $true;
        aclProtected = $acl.AreAccessRulesProtected; aclAllowedSids = $allowedSids;
        personalIdentifierLeaks = $identifierLeaks;
        diagnosticLogLines = $logLineCount;
        telemetrySamples = $sampleCount; actualSeconds = $actualSeconds;
        maximumGapMs = $maximumGapMs;
        gapsOverTwoSeconds = $gapsOverTwoSeconds; stageDeleted = $stageDeleted }
}
finally {
    if ($copyPath -and (Test-Path -LiteralPath $copyPath -PathType Leaf)) {
        [IO.File]::Delete($copyPath)
    }
    if ($stagedPath -and -not $stageDeleted -and
        [IO.Path]::GetDirectoryName($stagedPath) -eq $expectedPath -and
        (Test-Path -LiteralPath $stagedPath -PathType Leaf)) {
        $cleanupId = [Guid]::Empty
        if ([Guid]::TryParse([IO.Path]::GetFileNameWithoutExtension($stagedPath), [ref]$cleanupId)) {
            try {
                $cleanup = Invoke-Service 'deleteDiagnosticExport' $cleanupId ([ordered]@{ exportId = $cleanupId })
                $stageDeleted = $cleanup.deleted -eq $true -and -not (Test-Path -LiteralPath $stagedPath)
            }
            catch { Write-Warning "Service staging cleanup failed: $($_.Exception.Message)" }
        }
    }
    if ($stagedPath -and -not $stageDeleted -and (Test-Path -LiteralPath $stagedPath -PathType Leaf)) {
        Write-Warning "Staged diagnostic export still requires cleanup: $stagedPath"
    }
}
