#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('win-x64','win-arm64')]
    [string]$Runtime = 'win-x64',
    [switch]$SkipPublish
)
$ErrorActionPreference='Stop'
$Root=$PSScriptRoot
$Core=Join-Path $Root 'Core\SafeCopy_Core.ps1'
$Project=Join-Path $Root 'SafeCopy.Desktop.csproj'
$Smoke=Join-Path $Root 'QA\ServiceSmoke\SafeCopy.ServiceSmoke.csproj'
$Targeted=Join-Path $Root 'QA\Targeted\SafeCopy.TargetedQA.csproj'

function Pass([string]$m){ Write-Host "PASS  $m" -ForegroundColor Green }
function Fail([string]$m){ throw "FINAL QA FAILED: $m" }

Write-Host 'SafeCopy Desktop v4.7.2 final QA' -ForegroundColor Cyan
Write-Host "Root: $Root"

if (-not $IsWindows) { Fail 'This release QA harness must run on Windows.' }
if (-not (Get-Command dotnet.exe -ErrorAction SilentlyContinue)) { Fail '.NET 10 SDK is required.' }
if (-not (Get-Command pwsh.exe -ErrorAction SilentlyContinue)) { Fail 'PowerShell 7 is required.' }
if (-not (Get-Command robocopy.exe -ErrorAction SilentlyContinue)) { Fail 'robocopy.exe is required.' }

$dotnetMajor=[int]((& dotnet --version).Trim().Split('.')[0])
if($dotnetMajor -lt 10){ Fail ".NET 10+ required; found $(& dotnet --version)" }
Pass '.NET/PowerShell/Robocopy prerequisites'

# Parse, do not execute, the safety-critical engine.
$tokens=$null; $parseErrors=$null
[void][System.Management.Automation.Language.Parser]::ParseFile($Core,[ref]$tokens,[ref]$parseErrors)
if($parseErrors.Count -gt 0){ $parseErrors | Format-List | Out-String | Write-Host; Fail 'SafeCopy_Core.ps1 has PowerShell parse errors.' }
Pass 'PowerShell engine AST parse'

$coreText=Get-Content -LiteralPath $Core -Raw
foreach($switch in @('/MIR','/PURGE','/MOV','/MOVE')){
    # These strings are expected in comments/help and in the forbidden-switch guard, but must not be
    # literal members of the operational $RoboArgs assignment.
    $operational = [regex]::Match($coreText,'(?s)\$RoboArgs\s*=\s*@\((.*?)\)\s*\n\s*if \(\$IoRateMiB').Groups[1].Value
    if($operational -match [regex]::Escape("'$switch'") -or $operational -match [regex]::Escape('"'+$switch+'"')){ Fail "Destructive switch $switch is present in operational Robocopy args." }
}
Pass 'No mirror/purge/move switches in operational copy args'

Write-Host 'Restoring/building WPF project...' -ForegroundColor Cyan
& dotnet restore $Project -r $Runtime
if($LASTEXITCODE -ne 0){ Fail 'dotnet restore failed.' }
& dotnet build $Project -c Release -r $Runtime --self-contained true --no-restore
if($LASTEXITCODE -ne 0){ Fail 'WPF project build failed.' }
Pass 'WPF project compile'

Write-Host 'Running C# service smoke tests...' -ForegroundColor Cyan
& dotnet run --project $Smoke -c Release -r $Runtime --self-contained true
if($LASTEXITCODE -ne 0){ Fail 'C# service smoke tests failed.' }
Pass 'C# service smoke tests'

Write-Host 'Running targeted reliability regressions...' -ForegroundColor Cyan
& dotnet run --project $Targeted -c Release -r $Runtime --self-contained true
if($LASTEXITCODE -ne 0){ Fail 'Targeted reliability regressions failed.' }
Pass 'Targeted reliability regressions'

# Real SafeCopy engine test in disposable trees.
$QaRoot=Join-Path $env:TEMP ('SafeCopy-CoreQA-'+[guid]::NewGuid().ToString('N'))
$Src=Join-Path $QaRoot 'src'; $Dst=Join-Path $QaRoot 'dst'
New-Item -ItemType Directory -Path (Join-Path $Src 'nested'),(Join-Path $Src 'empty-dir'),$Dst,(Join-Path $Dst 'destination-only-dir') -Force | Out-Null
Set-Content -LiteralPath (Join-Path $Src 'a.txt') -Value 'source-A' -NoNewline
Set-Content -LiteralPath (Join-Path $Src 'nested\b.txt') -Value 'source-B' -NoNewline
Set-Content -LiteralPath (Join-Path $Src 'nested\ملف عربي.txt') -Value 'unicode-source' -NoNewline
Set-Content -LiteralPath (Join-Path $Dst 'a.txt') -Value 'dest-old' -NoNewline
Set-Content -LiteralPath (Join-Path $Dst 'destination-only.txt') -Value 'keep-me' -NoNewline
Set-Content -LiteralPath (Join-Path $Dst 'destination-only-dir\keep.txt') -Value 'keep-dir' -NoNewline

$AdsSupported=$false
try {
    Set-Content -LiteralPath (Join-Path $Src 'a.txt') -Stream 'SafeCopyQA' -Value 'named-stream-data' -NoNewline -ErrorAction Stop
    $AdsSupported=$true
} catch { Write-Host 'NOTE  NTFS named-stream QA skipped because the temporary source filesystem does not support it.' -ForegroundColor Yellow }
# Force same timestamp on the content conflict so independent verification/repair is exercised.
$t=(Get-Date).AddDays(-2)
(Get-Item (Join-Path $Src 'a.txt')).LastWriteTime=$t
(Get-Item (Join-Path $Dst 'a.txt')).LastWriteTime=$t

function Invoke-Core([switch]$VerifyOnly,[switch]$RetryFailed){
    $psi=[Diagnostics.ProcessStartInfo]::new('pwsh.exe')
    $psi.UseShellExecute=$false; $psi.CreateNoWindow=$true
    $psi.ArgumentList.Add('-NoLogo'); $psi.ArgumentList.Add('-NoProfile'); $psi.ArgumentList.Add('-ExecutionPolicy'); $psi.ArgumentList.Add('Bypass')
    $psi.ArgumentList.Add('-File'); $psi.ArgumentList.Add($Core)
    $psi.ArgumentList.Add('-Source'); $psi.ArgumentList.Add($Src)
    $psi.ArgumentList.Add('-Destination'); $psi.ArgumentList.Add($Dst)
    $psi.ArgumentList.Add('-Threads'); $psi.ArgumentList.Add('2')
    $psi.ArgumentList.Add('-VerifyThreads'); $psi.ArgumentList.Add('1')
    $psi.ArgumentList.Add('-RetryAttempts'); $psi.ArgumentList.Add('1')
    $psi.ArgumentList.Add('-RetryDelaySeconds'); $psi.ArgumentList.Add('0')
    $psi.ArgumentList.Add('-Yes')
    if($VerifyOnly){$psi.ArgumentList.Add('-VerifyOnly')}
    if($RetryFailed){$psi.ArgumentList.Add('-RetryFailed')}
    $p=[Diagnostics.Process]::new(); $p.StartInfo=$psi; [void]$p.Start(); $p.WaitForExit(); return $p.ExitCode
}

$ec=Invoke-Core
if($ec -ne 0){ Fail "Core Copy+Verify returned $ec" }
if((Get-FileHash (Join-Path $Src 'a.txt')).Hash -ne (Get-FileHash (Join-Path $Dst 'a.txt')).Hash){ Fail 'Source-wins content repair did not converge.' }
if(-not (Test-Path (Join-Path $Dst 'destination-only.txt'))){ Fail 'Destination-only file was removed.' }
if(-not (Test-Path (Join-Path $Dst 'destination-only-dir\keep.txt'))){ Fail 'Destination-only directory content was removed.' }
if(-not (Test-Path (Join-Path $Dst 'empty-dir'))){ Fail 'Empty source directory was not copied.' }
if((Get-FileHash (Join-Path $Src 'nested\ملف عربي.txt')).Hash -ne (Get-FileHash (Join-Path $Dst 'nested\ملف عربي.txt')).Hash){ Fail 'Unicode-path source file did not verify.' }
if(-not (Test-Path (Join-Path $Src 'a.txt'))){ Fail 'Source file was removed.' }
if($AdsSupported){
    $srcAds=(Get-Content -LiteralPath (Join-Path $Src 'a.txt') -Stream 'SafeCopyQA' -Raw)
    $dstAds=(Get-Content -LiteralPath (Join-Path $Dst 'a.txt') -Stream 'SafeCopyQA' -Raw)
    if($srcAds -ne $dstAds){ Fail 'Named stream content did not converge.' }
}
Pass 'Core Copy+Verify/source-wins/Unicode/empty-dir/ADS/destination-only preservation'

$ec=Invoke-Core -VerifyOnly
if($ec -ne 0){ Fail "Core VerifyOnly returned $ec" }
Pass 'Core VerifyOnly'

# Locked-source resumable verification test.
$locked=Join-Path $Src 'a.txt'
$guard=[IO.File]::Open($locked,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::None)
try { $lockedExit=Invoke-Core -VerifyOnly } finally { $guard.Dispose() }
if($lockedExit -eq 0){ Fail 'Locked-file verification unexpectedly reported full success.' }
$retryExit=Invoke-Core -RetryFailed
if($retryExit -ne 0){ Fail "RetryFailed returned $retryExit after lock was released." }
Pass 'Locked-file failure manifest + RetryFailed recovery'

Remove-Item -LiteralPath $QaRoot -Recurse -Force -ErrorAction SilentlyContinue

if(-not $SkipPublish){
    Write-Host 'Publishing single-file release...' -ForegroundColor Cyan
    & (Join-Path $Root 'Build.ps1') -Runtime $Runtime
    if($LASTEXITCODE -ne 0){ Fail 'Release publish failed.' }
    if(-not (Test-Path (Join-Path $Root 'dist\SafeCopy.exe'))){ Fail 'Published SafeCopy.exe missing.' }
    Pass 'Single-file publish'
}

Write-Host ''
Write-Host 'ALL AUTOMATED FINAL QA TESTS PASSED' -ForegroundColor Green
Write-Host 'Manual-only tests still required for VSS/UAC, physical USB eject, shutdown scheduling, and visual interaction.' -ForegroundColor Yellow
