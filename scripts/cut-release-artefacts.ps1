# cut-release-artefacts.ps1
#
# THE SIX FILES A RELEASE IS, in one command, because they were cut by hand from a
# command nobody wrote down. There are five publish-*.ps1 scripts in here for the ONE
# NuGet package and nothing at all for the six binaries people actually download - and
# the one time the hand-rolled command went wrong it shipped a win-x64 service EXE that
# started, served, downloaded models and then could not infer, because
# IncludeNativeLibrariesForSelfExtract had flattened the natives out of
# runtimes/<rid>/native/ where NativeLibraryResolver probes for them.
#
#   CircleAI-<ver>-vc<code>.apk                  the app,     64-bit (arm64-v8a + x86_64)
#   CircleAI-<ver>-vc<code>-32bit.apk            the app,     32-bit (armeabi-v7a + x86)
#   CircleAIService-<ver>-vc<code>.apk           the service, 64-bit
#   CircleAIService-<ver>-vc<code>-32bit.apk     the service, 32-bit
#   CircleAIService-Windows-<ver>-win-x64.exe    self-contained single file
#   CircleAIService-Windows-<ver>-win-x86.exe    self-contained single file
#
# THE FOUR WINDOWS FLAGS ARE ALL LOAD-BEARING AND THREE OF THEM HAVE BEEN A DEFECT:
#   PublishSingleFile              one file to download, which is the point of the asset.
#   IncludeAllContentForSelfExtract  unpacks the bundle PRESERVING relative paths, so
#                                  runtimes/<rid>/native/ survives. The alternative,
#                                  IncludeNativeLibrariesForSelfExtract, flattens them
#                                  to the root and the resolver finds nothing - that is
#                                  the 3.9.2 x64 EXE that could not infer.
#   EnableCompressionInSingleFile  without it the artefact is 165 MB instead of 75 MB.
#                                  Measured 2026-10-27: 164,883,577 -> 75,047,162 bytes.
#                                  Not cosmetic on a South African connection.
#   SelfContained                  no runtime to install on the target machine.
#
# Verification is not optional here, because every one of these failures published green:
# the PE machine type is read off each EXE, each APK's signer certificate is printed, and
# the foreign-native trim's own count is echoed from the build log.
#
# Usage:
#   .\cut-release-artefacts.ps1                 # all six, into artefacts\<version>\
#   .\cut-release-artefacts.ps1 -Only windows   # just the two EXEs
#   .\cut-release-artefacts.ps1 -Only android   # just the four APKs

[CmdletBinding()]
param(
    [ValidateSet('all','windows','android')]
    [string] $Only = 'all',

    [string] $OutDir
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent

# ── Microsoft's own MSBuild, found Microsoft's own way ───────────────────────────
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$msbuild = & $vswhere -latest -prerelease -products * -requires Microsoft.Component.MSBuild `
               -find 'MSBuild\**\Bin\amd64\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw "Visual Studio's MSBuild was not found (vswhere)" }

$svcProj = Join-Path $repo 'src\CircleAIService\CircleAIService.csproj'
$appProj = Join-Path $repo 'samples\CircleAI.Samples.Hybrid\CircleAI.Samples.App\CircleAI.Samples.App.csproj'

# ── The version, by evaluation and not by reading ───────────────────────────────
# The same rule publish-*.ps1 applies to the package: a cut that moves one property and
# leaves another produces artefacts whose name and whose stamp disagree.
function Prop([string]$Project, [string]$Name, [string[]]$Extra = @()) {
    (& $msbuild $Project "-getProperty:$Name" -nologo @Extra 2>&1 | Out-String).Trim()
}

# THE ASSET NAMES CARRY THE 3.9.x PACKAGE VERSION, NOT THE APP'S 1.1.0, and reading it
# off the service project gets the wrong one: CircleAIService.csproj deliberately
# overrides Version to 1.1.0 - "the apps version themselves; 3.9.x in
# Directory.Build.props is the package's" - which is what the EXE reports as its file
# version. Asked of CircleAI.csproj, which does not override it, exactly as
# publish-3.9.2.ps1 asks it. First run of this script produced
# CircleAIService-Windows-1.1.0-win-x64.exe and named six files after the wrong number.
$version = Prop (Join-Path $repo 'src\CircleAI\CircleAI.csproj') 'Version'
$svcCode = Prop $svcProj 'ApplicationVersion' @('-p:TargetFramework=net10.0-android')
$appCode = Prop $appProj 'ApplicationVersion' @('-p:TargetFramework=net10.0-android')
if (-not $version) { throw 'Version evaluated empty' }
if ($svcCode -ne $appCode) {
    throw "versionCode differs: service $svcCode, app $appCode. Both APKs carry the same vc in their names, so they must agree."
}
Write-Host "VERSION: $version   versionCode: $svcCode"

if (-not $OutDir) { $OutDir = Join-Path $repo "artefacts\$version" }
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
Write-Host "OUT:     $OutDir"

$light = @('-m:1', '-nodeReuse:false', '-lowPriority', '-p:UseSharedCompilation=false', '-v:n', '-nologo')
$built = [System.Collections.Generic.List[object]]::new()

# ── Windows ─────────────────────────────────────────────────────────────────────

function Publish-Windows([string]$Rid) {
    $stage = Join-Path $env:TEMP "circleai-pub-$Rid-$PID"
    $log   = Join-Path $env:TEMP "circleai-pub-$Rid-$PID.log"
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }

    Write-Host ""
    Write-Host "=== publish $Rid ==="
    & $msbuild $svcProj -restore -t:Publish `
        -p:Configuration=Release -p:TargetFramework=net10.0 "-p:RuntimeIdentifier=$Rid" `
        -p:SelfContained=true -p:PublishSingleFile=true `
        -p:IncludeAllContentForSelfExtract=true -p:EnableCompressionInSingleFile=true `
        "-p:PublishDir=$stage\" @light > $log 2>&1
    if ($LASTEXITCODE -ne 0) {
        Select-String -Path $log -Pattern ': error' | Select-Object -First 5 | ForEach-Object { Write-Host "  $($_.Line.Trim())" }
        throw "publish $Rid failed with exit $LASTEXITCODE (log: $log)"
    }

    # THE TRIM'S OWN COUNT, echoed rather than trusted. A win-x86 artefact carrying
    # runtimes/win-x64/native/MNN.dll looks complete and is 64-bit code under exactly
    # the name the resolver wants.
    $trim = Select-String -Path $log -Pattern 'dropped \d+ native' | Select-Object -First 1
    if ($trim) { Write-Host "  $($trim.Line.Trim())" }
    else       { Write-Host "  WARNING: the foreign-native trim reported nothing for $Rid" }

    # CIRCLEAI013: a publish with no bridge for its own RID will serve, download, and
    # fail on first model load. It is a warning by design, so it is surfaced here.
    Select-String -Path $log -Pattern 'CIRCLEAI013' | Select-Object -First 1 | ForEach-Object { Write-Host "  $($_.Line.Trim())" }

    $exe = Get-ChildItem $stage -Filter 'CircleAIService.exe' | Select-Object -First 1
    if (-not $exe) { throw "no CircleAIService.exe in $stage" }

    # PE MACHINE TYPE READ OFF THE ARTEFACT, not inferred from the RID we asked for.
    $bytes   = [IO.File]::ReadAllBytes($exe.FullName)
    $peOff   = [BitConverter]::ToInt32($bytes, 0x3C)
    $machine = [BitConverter]::ToUInt16($bytes, $peOff + 4)
    $want    = if ($Rid -eq 'win-x64') { 0x8664 } else { 0x014C }
    if ($machine -ne $want) {
        throw ("$Rid produced PE machine 0x{0:X4}, expected 0x{1:X4}" -f $machine, $want)
    }

    $dest = Join-Path $OutDir "CircleAIService-Windows-$version-$Rid.exe"
    Copy-Item $exe.FullName $dest -Force
    Remove-Item $stage -Recurse -Force
    Remove-Item $log -Force -ErrorAction SilentlyContinue

    $built.Add([pscustomobject]@{ Name = Split-Path $dest -Leaf; Bytes = (Get-Item $dest).Length; Detail = ("PE 0x{0:X4}" -f $machine) })
}

# ── Android ─────────────────────────────────────────────────────────────────────

function Publish-Apk([string]$Project, [string]$AppId, [string]$AssetStem, [switch]$Bits32, [string]$Password) {
    $abiArgs = @()
    $suffix  = ''
    if ($Bits32) { $abiArgs = @('-p:CircleAIAbi=32'); $suffix = '-32bit' }

    $log = Join-Path $env:TEMP "circleai-apk-$AssetStem$suffix-$PID.log"
    Write-Host ""
    Write-Host "=== build $AssetStem$suffix ==="

    # RESTORE WITHOUT A TargetFramework, BUILD WITH ONE, and they cannot be the same
    # invocation. `-restore -p:TargetFramework=net10.0-android` restores every referenced
    # project for that ONE framework, so a multi-targeting dependency comes out with an
    # assets file that has no net10.0 target and the build fails with NETSDK1005 -
    # "Ensure that restore has run", which it had, for the wrong target. The service APK
    # survived it and the app APK did not, because only the app references
    # CircleAI.Samples.Shared.
    & $msbuild $Project -t:Restore -p:Configuration=Release @abiArgs @light > $log 2>&1
    if ($LASTEXITCODE -ne 0) {
        Select-String -Path $log -Pattern ': error' | Select-Object -First 5 | ForEach-Object { Write-Host "  $($_.Line.Trim())" }
        throw "restore for $AssetStem$suffix failed with exit $LASTEXITCODE (log: $log)"
    }

    # A RELEASE BUILD WITH NO SIGNING PASSWORD SILENTLY DEBUG-SIGNS AND EXITS 0, which is
    # what CIRCLEAI012 exists to catch. The password is passed for this invocation only -
    # never setx, never echoed, never written to the log we keep.
    & $msbuild $Project -t:Build `
        -p:Configuration=Release -p:TargetFramework=net10.0-android `
        -p:AndroidPackageFormat=apk `
        "-p:AndroidSigningKeyPass=$Password" "-p:AndroidSigningStorePass=$Password" `
        @abiArgs @light >> $log 2>&1
    $code = $LASTEXITCODE

    # Redact before anything is shown, in case a diagnostic echoed the argument back.
    (Get-Content $log -Raw).Replace($Password, '<redacted>') | Set-Content $log
    if ($code -ne 0) {
        Select-String -Path $log -Pattern ': error' | Select-Object -First 5 | ForEach-Object { Write-Host "  $($_.Line.Trim())" }
        throw "$AssetStem$suffix failed with exit $code (log: $log)"
    }

    Select-String -Path $log -Pattern 'CIRCLEAI0\d\d' | Select-Object -First 4 | ForEach-Object { Write-Host "  $($_.Line.Trim())" }

    $apk = Join-Path (Split-Path $Project -Parent) "bin\Release\net10.0-android\$AppId-Signed.apk"
    if (-not (Test-Path $apk)) { throw "no signed APK at $apk" }

    # WHO SIGNED IT, read off the artefact. An unsigned Release comes out debug-signed and
    # installs and runs perfectly on a developer's phone, which is how it ships nowhere.
    $apksigner = Get-ChildItem "$env:LOCALAPPDATA\Android\Sdk\build-tools" -Filter 'apksigner.bat' -Recurse -ErrorAction SilentlyContinue |
                 Sort-Object FullName -Descending | Select-Object -First 1
    $signer = if ($apksigner) {
        $certs = & $apksigner.FullName verify --print-certs $apk 2>&1 | Out-String
        $cn = ([regex]::Match($certs, 'CN=([^,\r\n]+)')).Groups[1].Value
        if ($cn) { "signer CN=$cn" } else { 'signer UNREADABLE - check it by hand' }
    } else { 'apksigner not found - signer UNVERIFIED' }
    Write-Host "  $signer"
    if ($signer -match 'Android Debug') { throw "$AssetStem$suffix is DEBUG-SIGNED and can ship nowhere" }

    $dest = Join-Path $OutDir "$AssetStem-$version-vc$svcCode$suffix.apk"
    Copy-Item $apk $dest -Force
    Remove-Item $log -Force -ErrorAction SilentlyContinue

    $built.Add([pscustomobject]@{ Name = Split-Path $dest -Leaf; Bytes = (Get-Item $dest).Length; Detail = $signer })
}

# ── Run ─────────────────────────────────────────────────────────────────────────

if ($Only -in @('all','windows')) {
    Publish-Windows 'win-x64'
    Publish-Windows 'win-x86'
}

if ($Only -in @('all','android')) {
    $creds = 'C:\Dev\Solutions\com.bhengubv\thegeeknetwork\Deployment\deployment-credentials.ps1'
    if (-not (Test-Path $creds)) { throw "credentials file not found: $creds" }
    . $creds | Out-Null
    $pw = $SIGNING_CREDENTIALS.ANDROID_KEYSTORE_PASSWORD
    if (-not $pw) { throw 'SIGNING_CREDENTIALS.ANDROID_KEYSTORE_PASSWORD is empty' }

    # ONE ANDROID BUILD AT A TIME, same project: two at once on this workstation is how
    # both end up wrong, and the obj/ tree is shared between the 32- and 64-bit passes.
    Publish-Apk $svcProj 'com.bhengubv.circleai.service' 'CircleAIService' -Password $pw
    Publish-Apk $svcProj 'com.bhengubv.circleai.service' 'CircleAIService' -Bits32 -Password $pw
    Publish-Apk $appProj 'com.bhengubv.circleai.hybrid'  'CircleAI'        -Password $pw
    Publish-Apk $appProj 'com.bhengubv.circleai.hybrid'  'CircleAI'        -Bits32 -Password $pw
}

Write-Host ''
Write-Host "=== $($built.Count) artefact(s) in $OutDir ==="
$built | ForEach-Object { Write-Host ("  {0,-46} {1,14:N0} bytes   {2}" -f $_.Name, $_.Bytes, $_.Detail) }
