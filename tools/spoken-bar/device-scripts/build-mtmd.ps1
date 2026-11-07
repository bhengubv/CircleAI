# Builds the pinned fork's mtmd.dll (tools/bonsai-ref/mtmd) against the shipped DLLs' import
# libraries, with the toolchain that built them (VS 2022 BuildTools, x64), one node, low priority.
# Continue, not Stop: in Windows PowerShell a native tool's stderr line under 2>&1 would otherwise
# end the script; the exit codes below decide.
$ErrorActionPreference = 'Continue'
$worktree = 'C:\Dev\Solutions\com.bhengubv\CircleAI\.claude\worktrees\agent-a5582f0c55c8a4162'
$cmake = 'C:\Program Files\Microsoft Visual Studio\18\Insiders\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
$fork = 'C:\Dev\Solutions\com.bhengubv\CircleAI\native\llama-bridge\build-android-v7a\_deps\llama_cpp-src'
$libs = 'C:\Dev\Solutions\com.bhengubv\CircleAI\native\llama-bridge\build-x64\_deps\llama_cpp-build'
$build = '.\mtmd-build'

[System.Diagnostics.Process]::GetCurrentProcess().PriorityClass = 'BelowNormal'
& $cmake --version | Select-Object -First 1
& $cmake -S "$worktree\tools\bonsai-ref\mtmd" -B $build -G 'Visual Studio 17 2022' -A x64 `
    '-DCMAKE_GENERATOR_INSTANCE=C:/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools' `
    "-DFORK_SRC=$fork" "-DFORK_LIBS=$libs" 2>&1 | Select-Object -Last 15
if ($LASTEXITCODE -ne 0) { "CONFIGURE FAILED $LASTEXITCODE"; exit 1 }
& $cmake --build $build --config Release --target mtmd -- /m:1 /nodeReuse:false /v:m 2>&1 | Select-String -Pattern 'error|warning C4|mtmd.vcxproj ->|Build succeeded|Time Elapsed' | Select-Object -Last 40
if ($LASTEXITCODE -ne 0) { "BUILD FAILED $LASTEXITCODE"; exit 1 }
Get-Item "$build\Release\mtmd.dll" -ErrorAction SilentlyContinue | Select-Object FullName, Length, LastWriteTime
