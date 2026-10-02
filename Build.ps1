[CmdletBinding()]
param(
    [ValidateSet('win-x64','win-arm64')]
    [string]$Runtime = 'win-x64',

    [string]$CertificateThumbprint,

    [string]$TimestampUrl = 'http://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'

$Project = Join-Path $PSScriptRoot 'SafeCopy.Desktop.csproj'
$Dist = Join-Path $PSScriptRoot 'dist'

if (-not (Get-Command dotnet.exe -ErrorAction SilentlyContinue)) {
    throw '.NET 10 SDK or later is required to build SafeCopy Desktop. Install Microsoft.DotNet.SDK.10, then rerun Build.ps1.'
}

$VersionText = (& dotnet --version).Trim()
$Major = [int]($VersionText.Split('.')[0])
if ($Major -lt 10) {
    throw "SafeCopy Desktop requires .NET SDK 10 or later. Installed: $VersionText"
}

if (Test-Path $Dist) {
    Remove-Item -LiteralPath $Dist -Recurse -Force
}
New-Item -ItemType Directory -Path $Dist -Force | Out-Null

Write-Host "Building SafeCopy Desktop for $Runtime..." -ForegroundColor Cyan

& dotnet publish $Project `
    -c Release `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:PublishTrimmed=false `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $Dist

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

$Exe = Join-Path $Dist 'SafeCopy.exe'
if (-not (Test-Path -LiteralPath $Exe)) {
    throw "Build completed but SafeCopy.exe was not found in $Dist"
}

if (-not [string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
    $Thumb = ($CertificateThumbprint -replace '\s','').ToUpperInvariant()
    $Cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert |
        Where-Object { ($_.Thumbprint -replace '\s','').ToUpperInvariant() -eq $Thumb } |
        Select-Object -First 1

    if (-not $Cert) {
        throw "No CurrentUser code-signing certificate matched thumbprint $CertificateThumbprint"
    }

    Write-Host 'Signing SafeCopy.exe...' -ForegroundColor Cyan

    $SignTool = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($SignTool) {
        & $SignTool.Source sign /sha1 $Cert.Thumbprint /fd SHA256 /tr $TimestampUrl /td SHA256 $Exe
        if ($LASTEXITCODE -ne 0) {
            throw "signtool failed with exit code $LASTEXITCODE."
        }
    }
    else {
        $Signature = Set-AuthenticodeSignature `
            -FilePath $Exe `
            -Certificate $Cert `
            -HashAlgorithm SHA256 `
            -TimestampServer $TimestampUrl

        if ($Signature.Status -ne 'Valid') {
            throw "Authenticode signing failed: $($Signature.StatusMessage)"
        }
    }

    $Check = Get-AuthenticodeSignature -FilePath $Exe
    if ($Check.Status -ne 'Valid') {
        throw "Signature verification failed: $($Check.StatusMessage)"
    }
}

$Hash = (Get-FileHash -LiteralPath $Exe -Algorithm SHA256).Hash
Write-Host ''
Write-Host 'Build complete.' -ForegroundColor Green
Write-Host "EXE    : $Exe"
Write-Host "SHA256 : $Hash"

if ([string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
    Write-Host 'Signature: not requested' -ForegroundColor Yellow
}
else {
    Write-Host 'Signature: Valid' -ForegroundColor Green
}
