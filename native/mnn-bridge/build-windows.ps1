# build-windows.ps1
#
# Turnkey Windows build for mnnbridge:
#   1. Downloads the MNN 3.5.0 Windows bundle if not cached.
#   2. Extracts it into a sibling directory.
#   3. Runs CMake configure + build (Release, MultiThreadedDLL CRT to
#      match .NET's runtime CRT linkage).
#   4. Copies the resulting mnnbridge.dll + MNN.dll to
#      ../../src/CircleAI/Inference/runtimes/win-<arch>/native/
#      where the resolver and the NuGet .targets file expect them.
#
# Idempotent — re-run any time to refresh the build.
#
# THE DESTINATION MOVED AND THIS SCRIPT DID NOT.
# ──────────────────────────────────────────────────────────────────────────
# src/CircleAI.Inference/ became src/CircleAI/Inference/ at 3.8.1. This script
# still wrote to the old path until 2026-10-24, and the Resolve-Path ?? New-Item
# fallback below MEANT IT COULD NOT FAIL: it created src/CircleAI.Inference/ as
# a fresh orphan, copied two DLLs into it, printed "Build complete", and left
# the tree with no native at all. Same shape as the Android glob that matched
# nothing after the same move - see Directory.Build.targets.
#
# -Platform Win32 IS A HOOK, NOT A SUPPORTED PATH.
# ──────────────────────────────────────────────────────────────────────────
# Alibaba publishes no 32-bit Windows MNN in ANY release (checked 3.3.0 through
# 3.6.1 - every Windows asset is windows_x64). So -Platform Win32 cannot use a
# downloaded bundle: pass -MnnRoot pointing at an MNN you built from source for
# Win32. Without that this script will tell you the lib directory it wanted and
# stop, instead of linking the 64-bit import library and handing you a shim
# that never loads.

[CmdletBinding()]
param(
    [string] $MnnVersion = "3.5.0",
    [string] $MnnBundle  = "mnn_3.5.0_windows_x64_cpu_opencl.zip",
    [string] $MnnSha256  = "e37dbed6a5a6c26122239468d7fc8569d003c7f4a12c8a8024a33660fb13e4b7",
    [switch] $SkipDownload,
    [switch] $WithSmokeTest,
    [string] $Configuration = "Release",

    # x64 is the only architecture Alibaba ships. Win32 requires -MnnRoot.
    [ValidateSet("x64","Win32")]
    [string] $Platform = "x64",

    # Override the extracted-bundle root. Required for -Platform Win32.
    [string] $MnnRoot
)

$ErrorActionPreference = "Stop"

# win-x64 / win-x86 are the RID folder names; x64 / Win32 are MSVC's.
$ridArch = if ($Platform -eq "Win32") { "x86" } else { "x64" }

$bridgeRoot  = Split-Path -Parent $PSCommandPath
$cacheDir    = Join-Path $env:TEMP "mnnbridge-build-cache"
$bundleDir   = Join-Path $cacheDir ($MnnBundle -replace '\.zip$','')
$buildDir    = Join-Path $bridgeRoot "build-$Platform"

# Resolve the PARENT and then join, so a missing leaf is created where it
# belongs and a missing parent is an error rather than a new orphan tree.
$srcInference = Join-Path $bridgeRoot "..\..\src\CircleAI\Inference"
$srcInference = Resolve-Path -LiteralPath $srcInference -ErrorAction SilentlyContinue
if (-not $srcInference) {
    throw ("Cannot find src\CircleAI\Inference relative to $bridgeRoot. " +
           "The native payload location moved once already (src/CircleAI.Inference -> " +
           "src/CircleAI/Inference at 3.8.1); find where it lives now and fix this script " +
           "rather than letting it create a directory nobody reads.")
}
# Refuse BEFORE creating anything. An earlier draft of this block created the
# destination first and then threw, leaving an empty runtimes/win-x86/native
# behind on every failed attempt - the same litter this script was fixing.
if ($Platform -eq "Win32" -and -not $MnnRoot) {
    throw ("-Platform Win32 needs -MnnRoot: Alibaba ships no 32-bit Windows MNN bundle " +
           "in any release, so there is nothing to download. Build MNN from source with " +
           "cmake -A Win32 and point -MnnRoot at its install tree (the directory holding " +
           "lib\Win32\Release\Dynamic\MD\MNN.lib and include\).")
}

$nugetDest = Join-Path $srcInference "runtimes\win-$ridArch\native"
if (-not (Test-Path $nugetDest)) {
    $nugetDest = (New-Item -ItemType Directory -Path $nugetDest -Force).FullName
    Write-Host "Created $nugetDest (first build for win-$ridArch)."
}
$nugetDest = (Resolve-Path -LiteralPath $nugetDest).Path

if (-not (Test-Path $cacheDir)) {
    New-Item -ItemType Directory -Path $cacheDir | Out-Null
}

# ── 1. Download + verify MNN bundle ──────────────────────────────────────

# A caller-supplied -MnnRoot replaces download + extract entirely.
if ($MnnRoot) {
    if (-not (Test-Path $MnnRoot)) { throw "-MnnRoot does not exist: $MnnRoot" }
    $effectiveMnnRoot = (Resolve-Path -LiteralPath $MnnRoot).Path
    $expectedLibDir   = Join-Path $effectiveMnnRoot "lib\$Platform\Release\Dynamic\MD"
    if (-not (Test-Path (Join-Path $expectedLibDir 'MNN.lib'))) {
        throw ("No MNN.lib under $expectedLibDir. mnnbridge links against the import " +
               "library, so -MnnRoot must point at an MNN build laid out as " +
               "lib\$Platform\Release\Dynamic\MD\{MNN.lib,MNN.dll} with headers in include\.")
    }
    Write-Host "Using supplied MNN root: $effectiveMnnRoot ($Platform)"
}

$zipPath = Join-Path $cacheDir $MnnBundle
if ((-not $MnnRoot) -and (-not (Test-Path $zipPath)) -and (-not $SkipDownload)) {
    Write-Host "Downloading MNN $MnnVersion Windows bundle ..."
    $url = "https://github.com/alibaba/MNN/releases/download/$MnnVersion/$MnnBundle"
    Invoke-WebRequest -Uri $url -OutFile $zipPath -UseBasicParsing
}

if ((-not $MnnRoot) -and (Test-Path $zipPath)) {
    Write-Host "Verifying SHA-256 ..."
    $actual = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $MnnSha256.ToLowerInvariant()) {
        Write-Warning "SHA-256 mismatch — got $actual, expected $MnnSha256"
        Write-Warning "Continuing. If the build fails, delete $zipPath and re-run."
    } else {
        Write-Host "  SHA-256 OK"
    }
}

# ── 2. Extract ────────────────────────────────────────────────────────────

if (-not $MnnRoot) {
    $bundleCoreDll = "$bundleDir\lib\$Platform\Release\Dynamic\MD\MNN.dll"

    if (-not (Test-Path $bundleCoreDll)) {
        if (-not (Test-Path $zipPath)) {
            throw "MNN bundle not found at $zipPath and -SkipDownload was set."
        }
        Write-Host "Extracting bundle to $cacheDir ..."
        if (Test-Path $bundleDir) { Remove-Item -Recurse -Force $bundleDir }
        Expand-Archive -Path $zipPath -DestinationPath $cacheDir -Force
    }

    if (-not (Test-Path $bundleCoreDll)) {
        throw "Expected $bundleCoreDll after extract."
    }
    $effectiveMnnRoot = $bundleDir
}

$mnnCoreDll = "$effectiveMnnRoot\lib\$Platform\Release\Dynamic\MD\MNN.dll"
if (-not (Test-Path $mnnCoreDll)) {
    throw "No MNN.dll at $mnnCoreDll — cannot stage the core beside the bridge."
}

# ── 3. CMake configure + build ───────────────────────────────────────────

Write-Host "Configuring CMake ..."
if (Test-Path $buildDir) { Remove-Item -Recurse -Force $buildDir }

$cmakeArgs = @(
    "-S", $bridgeRoot,
    "-B", $buildDir,
    "-DMNN_ROOT=$effectiveMnnRoot",
    "-DMNN_WIN_ARCH=$Platform",
    "-A", $Platform
)
if ($WithSmokeTest) {
    $cmakeArgs += "-DMNNBRIDGE_BUILD_TEST=ON"
}

& cmake @cmakeArgs
if ($LASTEXITCODE -ne 0) { throw "CMake configure failed." }

Write-Host "Building ($Configuration) ..."
& cmake --build $buildDir --config $Configuration
if ($LASTEXITCODE -ne 0) { throw "CMake build failed." }

$bridgeDll = Join-Path $buildDir "$Configuration\mnnbridge.dll"
if (-not (Test-Path $bridgeDll)) {
    throw "Expected $bridgeDll but did not find it."
}

# ── 4. Copy to NuGet payload location ────────────────────────────────────

Copy-Item $bridgeDll  $nugetDest -Force
Copy-Item $mnnCoreDll $nugetDest -Force

# Prove the bitness of what was just staged rather than trusting -A. The PE
# machine type sits at the offset stored in the 4 bytes at 0x3C, plus 4:
# 0x014C = x86, 0x8664 = x64, 0xAA64 = arm64.
function Get-PeMachine([string] $Path) {
    $fs = [System.IO.File]::OpenRead($Path)
    try {
        $br = New-Object System.IO.BinaryReader($fs)
        $fs.Position = 0x3C
        $peOffset = $br.ReadInt32()
        $fs.Position = $peOffset + 4
        return $br.ReadUInt16()
    } finally { $fs.Dispose() }
}
$expectedMachine = if ($Platform -eq "Win32") { 0x014C } else { 0x8664 }
foreach ($staged in @("mnnbridge.dll","MNN.dll")) {
    $p = Join-Path $nugetDest $staged
    $m = Get-PeMachine $p
    if ($m -ne $expectedMachine) {
        # -f binds tighter than +, so the format string must be ONE literal.
        throw ("{0} in {1} is PE machine 0x{2:X4}, expected 0x{3:X4} for -Platform {4}. Staging it would ship a native the runtime cannot load." `
               -f $staged, $nugetDest, $m, $expectedMachine, $Platform)
    }
    Write-Host ("  verified {0,-14} PE machine 0x{1:X4}" -f $staged, $m)
}

Write-Host ""
Write-Host "Build complete ($Platform -> win-$ridArch)."
Write-Host ("  mnnbridge.dll  -> {0}\mnnbridge.dll" -f $nugetDest)
Write-Host ("  MNN.dll        -> {0}\MNN.dll" -f $nugetDest)
Write-Host ""
Write-Host "Next: dotnet pack src\CircleAI. The .targets file pulls both DLLs"
Write-Host ("into the package's runtimes/win-{0}/native/ folder." -f $ridArch)
