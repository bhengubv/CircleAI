# S8 vision, one locked job: the fork's mtmd.dll, the fork's vision reference, then the test
# project (net10.0 Release) and the vision tests. Each step says how it went; a failed native build
# skips the reference but not the C# build.
$ErrorActionPreference = 'Continue'
$worktree = 'C:\Dev\Solutions\com.bhengubv\CircleAI\.claude\worktrees\agent-a5582f0c55c8a4162'
$scratch = '.'
$msb = 'C:\Program Files\Microsoft Visual Studio\18\Insiders\MSBuild\Current\Bin\amd64\MSBuild.exe'
[System.Diagnostics.Process]::GetCurrentProcess().PriorityClass = 'BelowNormal'
$t0 = Get-Date

"== 1. mtmd.dll"
try { & "$scratch\build-mtmd.ps1" } catch { "   mtmd build threw: $($_.Exception.Message)" }
$mtmd = "$scratch\mtmd-build\Release\mtmd.dll"
"   ($([int]((Get-Date) - $t0).TotalSeconds) s)"

if ((Test-Path $mtmd) -and -not $env:S8_SKIP_REFERENCE) {
    "== 2. the fork's vision reference"
    $models = "$env:LOCALAPPDATA\CircleAI\models\Ternary-Bonsai-2-2B"
    Push-Location $worktree
    dotnet run --project tools/bonsai-ref -c Release -p:RestoreIgnoreFailedSources=true "-p:MtmdDll=$mtmd" -- vision `
        "$models\Ternary-Bonsai-2-2B-PTQ1_0.gguf" "$models\Ternary-Bonsai-2-2B-mmproj-Q8_0.gguf" `
        fixtures/bonsai2/2B/vision-shapes.png fixtures/bonsai2/2B/vision.json 2>&1 |
        Tee-Object -FilePath "$scratch\s8-reference.log" | Select-Object -Last 30
    "   reference exit $LASTEXITCODE ($([int]((Get-Date) - $t0).TotalSeconds) s)"
    Pop-Location
} else {
    "== 2. SKIPPED: no mtmd.dll"
}

"== 3. the test project, net10.0 Release"
$proj = "$worktree\tests\CircleAI.Tests\CircleAI.Tests.csproj"
& $msb $proj -t:Restore -low -m:1 -nodeReuse:false -p:Configuration=Release -p:RestoreIgnoreFailedSources=true -v:m -nologo 2>&1 |
    Set-Content -Path "$scratch\s8-restore.log" -Encoding utf8
"   restore exit $LASTEXITCODE"
& $msb $proj -low -m:1 -nodeReuse:false -p:UseSharedCompilation=false -p:Configuration=Release -p:TargetFramework=net10.0 -v:m -nologo 2>&1 |
    Set-Content -Path "$scratch\s8-build.log" -Encoding utf8
$build = $LASTEXITCODE
"   build exit $build ($([int]((Get-Date) - $t0).TotalSeconds) s)"
@(Select-String -Path "$scratch\s8-build.log" -Pattern ': error ' | Select-Object -ExpandProperty Line -Unique) | Select-Object -First 30

if ($build -eq 0) {
    "== 4. the vision tests"
    $dll = "$worktree\tests\CircleAI.Tests\bin\Release\net10.0\CircleAI.Tests.dll"
    $filter = 'FullyQualifiedName~ManagedVisionTests|FullyQualifiedName~WeightMatrixTests|FullyQualifiedName~VisionImageTests|FullyQualifiedName~ManagedChatGeneratorTests.System_messages'
    dotnet test $dll --filter $filter --logger 'console;verbosity=detailed' 2>&1 | Set-Content -Path "$scratch\s8-test.log" -Encoding utf8
    "   test exit $LASTEXITCODE ($([int]((Get-Date) - $t0).TotalSeconds) s)"
    @(Select-String -Path "$scratch\s8-test.log" -Pattern 'Passed |Failed |Total tests|Passed!|Failed!|Aborted') | Select-Object -ExpandProperty Line | Select-Object -First 40
}
