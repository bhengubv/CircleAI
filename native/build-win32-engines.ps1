# build-win32-engines.ps1
#
# Builds BOTH inference engines for 32-bit Windows and stages them into
# src/CircleAI/Inference/runtimes/win-x86/native/.
#
# WHY THIS SCRIPT EXISTS
# ──────────────────────────────────────────────────────────────────────────
# win-x86 shipped for a long time with NO engine at all. The publish started,
# served, downloaded models and then failed on the first model load, and
# CIRCLEAI013 was added to say so at build time. This is the script that makes
# CIRCLEAI013 stop firing, because it produces the thing the guard looks for.
#
# AFRICA IS MOSTLY 32-BIT. That is the reason this matters and it is not an
# edge case: the 32-bit service is the product on most of the hardware it is
# aimed at. It is not a legacy build to document around.
#
# NEITHER ENGINE HAS AN UPSTREAM 32-BIT WINDOWS BINARY.
#   MNN       - checked all 63 alibaba/MNN releases on 2026-10-27: every single
#               Windows asset is windows_x64. There has never been a 32-bit one.
#   llama.cpp - we build it from the pinned fork anyway (llama-bridge does this
#               for Android already), so Win32 is just another target.
# So both are built from source here. First done 2026-10-27, on the dev laptop.
#
# MNN NEEDS ONE SOURCE FIX AND IT IS WHY NO 32-BIT MNN EXISTS.
# transformers/llm/engine/src/httplib.h does this, unconditionally, under
# _MSC_VER:
#
#     using ssize_t = __int64;
#
# On 64-bit MSVC nothing else defines ssize_t, so the line is load-bearing. On
# 32-bit MSVC the CRT has already defined it as int, so this is a redefinition
# with a different basic type and the llm target dies with C2371. One line, and
# it blocks the entire 32-bit build. Applied below as a guarded replacement
# rather than a .patch file, because a patch that no longer applies is worse
# than a check that reports.
#
# THE LLM MUST BE INSIDE MNN.dll. mnn-bridge/CMakeLists.txt links only MNN.lib
# on Windows - there is no separate llm.lib in that link line, unlike Android
# where libllm.so is linked explicitly. Hence MNN_SEP_BUILD=OFF. Verified after
# the build by counting exports that name Llm; 61 of them on the first build.
#
# MD, NOT MT. .NET's runtime is MD. Mixing CRTs is a load-time failure, so
# MNN_WIN_RUNTIME_MT stays OFF.
#
# USAGE
#   .\build-win32-engines.ps1 -MnnSrc <MNN-3.5.0 source> -LlamaSrc <llama.cpp source>
#
# Neither source tree is vendored - MNN comes from the 3.5.0 tarball, llama.cpp
# from the tag llama-bridge/CMakeLists.txt pins. Both are deleted after staging
# in normal use; the staged DLLs are what the repo keeps.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $MnnSrc,
    [Parameter(Mandatory = $true)] [string] $LlamaSrc,
    [string] $Configuration = 'Release',
    [int]    $Jobs = 2,
    [switch] $SkipMnn,
    [switch] $SkipLlama
)

$ErrorActionPreference = 'Stop'

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$dest     = Join-Path $repoRoot 'src\CircleAI\Inference\runtimes\win-x86\native'
if (-not (Test-Path $dest)) { New-Item -ItemType Directory -Force -Path $dest | Out-Null }
$dest = (Resolve-Path $dest).Path

# PE machine type, read rather than trusted. -A Win32 is an intention; this is
# the fact. 0x014C = x86, 0x8664 = x64.
function Get-PeMachine([string] $Path) {
    $fs = [IO.File]::OpenRead($Path)
    try {
        $br = New-Object IO.BinaryReader($fs)
        $fs.Position = 0x3C
        $pe = $br.ReadInt32()
        $fs.Position = $pe + 4
        return $br.ReadUInt16()
    } finally { $fs.Dispose() }
}

function Assert-X86([string] $Path) {
    $m = Get-PeMachine $Path
    if ($m -ne 0x014C) {
        throw ("{0} is PE machine 0x{1:X4}, expected 0x014C. Staging it would ship a native the 32-bit runtime cannot load." -f $Path, $m)
    }
    Write-Host ("  verified {0,-20} PE 0x{1:X4}" -f (Split-Path $Path -Leaf), $m)
}

# ── MNN ───────────────────────────────────────────────────────────────────
if (-not $SkipMnn) {
    if (-not (Test-Path (Join-Path $MnnSrc 'CMakeLists.txt'))) { throw "No MNN source at $MnnSrc" }

    # The one source fix. Idempotent: if the guard is already there, say so and
    # move on, so re-running this script is safe.
    $httplib = Join-Path $MnnSrc 'transformers\llm\engine\src\httplib.h'
    if (-not (Test-Path $httplib)) { throw "Expected $httplib - MNN's layout moved; fix this script rather than guessing." }
    $lines = [IO.File]::ReadAllLines($httplib)
    $bad   = -1
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i].Trim() -eq 'using ssize_t = __int64;') { $bad = $i; break }
    }
    if ($bad -ge 0) {
        $lines[$bad] = "#if defined(_WIN64)`r`nusing ssize_t = __int64;`r`n#endif"
        [IO.File]::WriteAllLines($httplib, $lines)
        Write-Host "  patched httplib.h:$($bad + 1) - ssize_t typedef is now _WIN64 only"
    } elseif (($lines | Where-Object { $_ -match '^\s*#if defined\(_WIN64\)' }) -and ($lines | Where-Object { $_.Trim() -eq 'using ssize_t = __int64;' }).Count -eq 0) {
        Write-Host "  httplib.h already guarded - nothing to patch"
    } else {
        Write-Host "  httplib.h: no unconditional ssize_t typedef found. Upstream may have fixed it; continuing."
    }

    $mnnBuild = Join-Path $MnnSrc '..\mnn-build-win32'
    Write-Host "Configuring MNN (Win32) ..."
    & cmake -S $MnnSrc -B $mnnBuild -A Win32 `
        -DMNN_BUILD_SHARED_LIBS=ON -DMNN_SEP_BUILD=OFF -DMNN_BUILD_LLM=ON `
        -DMNN_LOW_MEMORY=ON -DMNN_SUPPORT_TRANSFORMER_FUSE=ON -DMNN_WIN_RUNTIME_MT=OFF `
        -DMNN_OPENCL=OFF -DMNN_VULKAN=OFF -DMNN_BUILD_OPENCV=OFF `
        -DMNN_BUILD_TEST=OFF -DMNN_BUILD_DEMO=OFF -DMNN_BUILD_TOOLS=OFF `
        -DMNN_BUILD_QUANTOOLS=OFF -DMNN_BUILD_CONVERTER=OFF -DMNN_BUILD_BENCHMARK=OFF
    if ($LASTEXITCODE -ne 0) { throw "MNN configure failed." }

    Write-Host "Building MNN ($Configuration, $Jobs jobs) ..."
    & cmake --build $mnnBuild --config $Configuration --parallel $Jobs
    if ($LASTEXITCODE -ne 0) { throw "MNN build failed." }

    $mnnDll = Join-Path $mnnBuild "$Configuration\MNN.dll"
    $mnnLib = Join-Path $mnnBuild "$Configuration\MNN.lib"
    foreach ($f in @($mnnDll, $mnnLib)) { if (-not (Test-Path $f)) { throw "Expected $f" } }
    Assert-X86 $mnnDll

    # Lay it out as mnn-bridge/CMakeLists.txt:54 and build-windows.ps1:96 expect,
    # so the existing script can consume it with no special casing.
    $install = Join-Path $MnnSrc '..\mnn-win32-install'
    $libDir  = Join-Path $install 'lib\Win32\Release\Dynamic\MD'
    New-Item -ItemType Directory -Force -Path $libDir | Out-Null
    Copy-Item $mnnDll $libDir -Force
    Copy-Item $mnnLib $libDir -Force
    $incDir = Join-Path $install 'include'
    New-Item -ItemType Directory -Force -Path $incDir | Out-Null
    Copy-Item (Join-Path $MnnSrc 'include\*') $incDir -Recurse -Force

    # The bridge build is the repo's existing script. Nothing new here.
    Write-Host "Building mnnbridge via build-windows.ps1 -Platform Win32 ..."
    & (Join-Path $PSScriptRoot 'mnn-bridge\build-windows.ps1') -Platform Win32 -MnnRoot (Resolve-Path $install).Path
    if ($LASTEXITCODE -ne 0) { throw "mnnbridge Win32 build failed." }
}

# ── llama.cpp + llamabridge ───────────────────────────────────────────────
if (-not $SkipLlama) {
    if (-not (Test-Path (Join-Path $LlamaSrc 'CMakeLists.txt'))) { throw "No llama.cpp source at $LlamaSrc" }

    $lbRoot  = Join-Path $PSScriptRoot 'llama-bridge'
    $lbBuild = Join-Path $lbRoot 'build-win32'

    # FETCHCONTENT_SOURCE_DIR_LLAMA_CPP points FetchContent at a tree already on
    # disk, so cmake spawns no git. That matters: a killed build used to orphan
    # git children that kept hammering the disk after the parent was gone.
    Write-Host "Configuring llamabridge (Win32) ..."
    & cmake -S $lbRoot -B $lbBuild -A Win32 `
        "-DFETCHCONTENT_SOURCE_DIR_LLAMA_CPP=$((Resolve-Path $LlamaSrc).Path)" `
        -DGGML_NATIVE=OFF -DGGML_AVX=OFF -DGGML_AVX2=OFF -DGGML_F16C=OFF -DGGML_FMA=OFF `
        -DLLAMA_BUILD_TESTS=OFF -DLLAMA_BUILD_EXAMPLES=OFF -DLLAMA_BUILD_SERVER=OFF `
        -DLLAMA_BUILD_TOOLS=OFF -DLLAMA_CURL=OFF
    if ($LASTEXITCODE -ne 0) { throw "llamabridge configure failed." }

    # SIMD OFF ON PURPOSE. GGML_NATIVE=ON bakes in whatever this build machine
    # supports, and a 32-bit target is exactly where the oldest CPUs are. An
    # AVX2 binary on a machine without it is an illegal instruction, not a
    # slower answer.
    Write-Host "Building llamabridge ($Configuration, $Jobs jobs) ..."
    & cmake --build $lbBuild --config $Configuration --parallel $Jobs
    if ($LASTEXITCODE -ne 0) { throw "llamabridge build failed." }

    # llamabridge.dll needs llama.dll, which needs the three ggml libraries.
    # Staging the shim alone is a dlopen failure one link further down - the same
    # mistake Directory.Build.targets warns about for whisper's ggml siblings.
    foreach ($n in 'llamabridge.dll','llama.dll','ggml.dll','ggml-base.dll','ggml-cpu.dll') {
        $f = Get-ChildItem $lbBuild -Recurse -File -Filter $n -ErrorAction SilentlyContinue |
             Where-Object { $_.FullName -match [regex]::Escape($Configuration) } | Select-Object -First 1
        if (-not $f) { throw "llamabridge build produced no $n" }
        Copy-Item $f.FullName $dest -Force
        Assert-X86 (Join-Path $dest $n)
    }
}

Write-Host ""
Write-Host "win-x86 native payload:"
Get-ChildItem $dest -File | Sort-Object Name | ForEach-Object {
    Write-Host ("  {0,-22} {1,12:N0} bytes  PE 0x{2:X4}" -f $_.Name, $_.Length, (Get-PeMachine $_.FullName))
}
Write-Host ""
Write-Host "CIRCLEAI013 fires when runtimes/win-x86/native/mnnbridge.dll is absent."
Write-Host "If mnnbridge.dll is listed above, a win-x86 publish will no longer warn."
