# build-windows.ps1
#
# Turnkey Windows x64 build for mnnbridge:
#   1. Downloads the MNN 3.5.0 Windows bundle if not cached.
#   2. Extracts it into a sibling directory.
#   3. Runs CMake configure + build (Release, MultiThreadedDLL CRT to
#      match .NET's runtime CRT linkage).
#   4. Copies the resulting mnnbridge.dll + MNN.dll to
#      ../../src/CircleAI.Inference/runtimes/win-x64/native/
#      where the NuGet .targets file expects them.
#
# Idempotent — re-run any time to refresh the build.

[CmdletBinding()]
param(
    [string] $MnnVersion = "3.5.0",
    [string] $MnnBundle  = "mnn_3.5.0_windows_x64_cpu_opencl.zip",
    [string] $MnnSha256  = "e37dbed6a5a6c26122239468d7fc8569d003c7f4a12c8a8024a33660fb13e4b7",
    [switch] $SkipDownload,
    [switch] $WithSmokeTest,
    [string] $Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$bridgeRoot  = Split-Path -Parent $PSCommandPath
$cacheDir    = Join-Path $env:TEMP "mnnbridge-build-cache"
$bundleDir   = Join-Path $cacheDir ($MnnBundle -replace '\.zip$','')
$buildDir    = Join-Path $bridgeRoot "build"
$nugetDest   = Join-Path $bridgeRoot "..\..\src\CircleAI.Inference\runtimes\win-x64\native"
$nugetDest   = (Resolve-Path -LiteralPath $nugetDest -ErrorAction SilentlyContinue) `
                ?? (New-Item -ItemType Directory -Path $nugetDest -Force).FullName

if (-not (Test-Path $cacheDir)) {
    New-Item -ItemType Directory -Path $cacheDir | Out-Null
}

# ── 1. Download + verify MNN bundle ──────────────────────────────────────

$zipPath = Join-Path $cacheDir $MnnBundle
if ((-not (Test-Path $zipPath)) -and (-not $SkipDownload)) {
    Write-Host "Downloading MNN $MnnVersion Windows bundle ..."
    $url = "https://github.com/alibaba/MNN/releases/download/$MnnVersion/$MnnBundle"
    Invoke-WebRequest -Uri $url -OutFile $zipPath -UseBasicParsing
}

if (Test-Path $zipPath) {
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

if (-not (Test-Path "$bundleDir\lib\x64\Release\Dynamic\MD\MNN.dll")) {
    if (-not (Test-Path $zipPath)) {
        throw "MNN bundle not found at $zipPath and -SkipDownload was set."
    }
    Write-Host "Extracting bundle to $cacheDir ..."
    if (Test-Path $bundleDir) { Remove-Item -Recurse -Force $bundleDir }
    Expand-Archive -Path $zipPath -DestinationPath $cacheDir -Force
}

if (-not (Test-Path "$bundleDir\lib\x64\Release\Dynamic\MD\MNN.dll")) {
    throw "Expected $bundleDir\lib\x64\Release\Dynamic\MD\MNN.dll after extract."
}

# ── 3. CMake configure + build ───────────────────────────────────────────

Write-Host "Configuring CMake ..."
if (Test-Path $buildDir) { Remove-Item -Recurse -Force $buildDir }

$cmakeArgs = @(
    "-S", $bridgeRoot,
    "-B", $buildDir,
    "-DMNN_ROOT=$bundleDir",
    "-A", "x64"
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

Copy-Item $bridgeDll                                            $nugetDest -Force
Copy-Item "$bundleDir\lib\x64\Release\Dynamic\MD\MNN.dll"       $nugetDest -Force

Write-Host ""
Write-Host "Build complete."
Write-Host ("  mnnbridge.dll  -> {0}\mnnbridge.dll" -f $nugetDest)
Write-Host ("  MNN.dll        -> {0}\MNN.dll" -f $nugetDest)
Write-Host ""
Write-Host "Next: dotnet pack CircleAI.Inference. The .targets file pulls"
Write-Host "both DLLs into the package's runtimes/win-x64/native/ folder."
