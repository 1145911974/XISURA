Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSEdition -eq 'Desktop') {
    throw 'Use the configured .NET pwsh runtime; Windows PowerShell 5.1 does not provide System.Security.Cryptography.Pkcs.'
}

function ConvertTo-HexString {
    param([byte[]]$Bytes)
    return (-join ($Bytes | ForEach-Object { $_.ToString('X2') }))
}

function Test-ExactBytes {
    param([byte[]]$Left, [byte[]]$Right)
    if ($Left.Length -ne $Right.Length) { return $false }
    $difference = 0
    for ($index = 0; $index -lt $Left.Length; $index++) {
        $difference = $difference -bor ($Left[$index] -bxor $Right[$index])
    }
    return $difference -eq 0
}

function Get-Sha256Hex {
    param([byte[]]$Bytes)
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try { return ConvertTo-HexString $sha256.ComputeHash($Bytes) } finally { $sha256.Dispose() }
}

if ($args.Count -ne 1) {
    throw 'Exactly one manifest path is required.'
}

$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$manifestRoot = (Resolve-Path -LiteralPath (Join-Path $repoRoot 'src\Jiaolong.Hardware.Mechrevo\Compatibility\Manifests')).Path
$manifestInput = [string]$args[0]
$manifestCandidate = if ([IO.Path]::IsPathRooted($manifestInput)) {
    $manifestInput
} else {
    Join-Path $repoRoot $manifestInput
}
$manifestPath = (Resolve-Path -LiteralPath $manifestCandidate).Path
$manifestRootPrefix = $manifestRoot.TrimEnd('\') + '\'

if (-not $manifestPath.StartsWith($manifestRootPrefix, [StringComparison]::OrdinalIgnoreCase) -or
    [IO.Path]::GetExtension($manifestPath) -ne '.json') {
    throw 'Manifest path must be a JSON file under the compatibility manifest directory.'
}

$manifestItem = Get-Item -LiteralPath $manifestPath
if (($manifestItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw 'Reparse-point manifests are not accepted.'
}

$schemaPath = Join-Path $repoRoot 'src\Jiaolong.Hardware.Mechrevo\Compatibility\Schema\compatibility-manifest.schema.json'
if (-not (Test-Path -LiteralPath $schemaPath -PathType Leaf)) {
    throw 'Compatibility manifest schema is missing.'
}
$schema = Get-Content -LiteralPath $schemaPath -Raw | ConvertFrom-Json
if ($schema.'$schema' -notlike 'https://json-schema.org/*' -or $schema.properties.schemaVersion.const -ne 1) {
    throw 'Compatibility manifest schema is invalid.'
}

$content = [IO.File]::ReadAllBytes($manifestPath)
$utf8 = [Text.UTF8Encoding]::new($false, $true)
$json = $utf8.GetString($content)
$model = $json | ConvertFrom-Json
$required = @('schemaVersion', 'manifestId', 'deviceFamily', 'boardProductsExact', 'biosVersionsExact', 'cpu', 'gpus', 'wmiProvider', 'dependencies', 'safety', 'capabilities', 'migration')
foreach ($name in $required) {
    if ($null -eq $model.PSObject.Properties[$name]) {
        throw "Manifest is missing required property '$name'."
    }
}
if ($model.schemaVersion -ne 1 -or [string]::IsNullOrWhiteSpace([string]$model.manifestId) -or
    @($model.boardProductsExact).Count -eq 0 -or @($model.biosVersionsExact).Count -eq 0 -or
    @($model.gpus).Count -eq 0) {
    throw 'Manifest does not satisfy schemaVersion 1 required values.'
}

$anchorPath = Join-Path $repoRoot 'src\Jiaolong.Hardware.Mechrevo\Compatibility\Signing\Jiaolong.ManifestSigning.cer'
$anchorDer = [IO.File]::ReadAllBytes($anchorPath)
$anchorHash = Get-Sha256Hex $anchorDer
$certificates = @(Get-ChildItem Cert:\CurrentUser\My | Where-Object Subject -eq 'CN=Jiaolong Control Center Manifest Signing')
if ($certificates.Count -ne 1) {
    throw 'Exactly one manifest signing certificate must exist in CurrentUser\My.'
}
$cert = $certificates[0]
if (-not (Test-ExactBytes $cert.RawData $anchorDer) -or -not $cert.HasPrivateKey) {
    throw 'The CurrentUser signing certificate does not match the committed public certificate.'
}
$privateKey = $cert.PrivateKey
if ($privateKey -is [Security.Cryptography.RSACng]) {
    if (($privateKey.Key.ExportPolicy -band [Security.Cryptography.CngExportPolicies]::AllowExport) -ne 0) {
        throw 'The signing certificate must use a non-exportable CNG private key.'
    }
} elseif ($null -eq $privateKey) {
    $certutilInfo = (& certutil.exe -user -silent -store My $cert.Thumbprint 2>&1 | Out-String)
    if ($certutilInfo -notmatch 'Microsoft Software Key Storage Provider') {
        throw 'The signing certificate must use Microsoft Software Key Storage Provider.'
    }
} else {
    throw 'The signing certificate must use a CNG private key.'
}

Add-Type -AssemblyName System.Security.Cryptography.Pkcs
$cms = [Security.Cryptography.Pkcs.SignedCms]::new(
    [Security.Cryptography.Pkcs.ContentInfo]::new($content),
    $true)
$signer = [Security.Cryptography.Pkcs.CmsSigner]::new(
    [Security.Cryptography.Pkcs.SubjectIdentifierType]::IssuerAndSerialNumber,
    $cert)
$signer.IncludeOption = [Security.Cryptography.X509Certificates.X509IncludeOption]::EndCertOnly
$signer.DigestAlgorithm = [Security.Cryptography.Oid]::new('2.16.840.1.101.3.4.2.1', 'SHA256')
$cms.ComputeSignature($signer)
$signature = $cms.Encode()

$signaturePath = [IO.Path]::ChangeExtension($manifestPath, '.json.p7s')
$temporaryPath = Join-Path (Split-Path -Path $signaturePath -Parent) ("$([Guid]::NewGuid().ToString('N')).tmp")
$stream = $null
try {
    $stream = [IO.File]::Open($temporaryPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    $stream.Write($signature, 0, $signature.Length)
    $stream.Flush($true)
    $stream.Dispose()
    $stream = $null
    if (Test-Path -LiteralPath $signaturePath -PathType Leaf) {
        [IO.File]::Move($temporaryPath, $signaturePath, $true)
    } else {
        [IO.File]::Move($temporaryPath, $signaturePath)
    }
} finally {
    if ($null -ne $stream) { $stream.Dispose() }
    if (Test-Path -LiteralPath $temporaryPath -PathType Leaf) {
        Remove-Item -LiteralPath $temporaryPath -Force
    }
}

[pscustomobject]@{
    Manifest = $manifestPath
    ManifestSha256 = Get-Sha256Hex $content
    CertificateSha256 = $anchorHash
    Signature = $signaturePath
    SignatureLength = $signature.Length
}
