# S8 vision: the whole CircleAI.Tests suite on net10.0 then net9.0, as the lock script runs one leg,
# in one lock. Each leg says its build and test exits and its counts.
$ErrorActionPreference = 'Continue'
$worktree = 'C:\Dev\Solutions\com.bhengubv\CircleAI\.claude\worktrees\agent-a5582f0c55c8a4162'
$scratch = '.'
$msb = 'C:\Program Files\Microsoft Visual Studio\18\Insiders\MSBuild\Current\Bin\amd64\MSBuild.exe'
[System.Diagnostics.Process]::GetCurrentProcess().PriorityClass = 'BelowNormal'
$proj = "$worktree\tests\CircleAI.Tests\CircleAI.Tests.csproj"
foreach ($fw in 'net10.0', 'net9.0') {
    $t0 = Get-Date
    "== $fw"
    & $msb $proj -low -m:1 -nodeReuse:false -p:UseSharedCompilation=false -p:Configuration=Release "-p:TargetFramework=$fw" -v:m -nologo 2>&1 |
        Set-Content -Path "$scratch\s8-full-build-$fw.log" -Encoding utf8
    $build = $LASTEXITCODE
    "   build exit $build ($([int]((Get-Date) - $t0).TotalSeconds) s)"
    @(Select-String -Path "$scratch\s8-full-build-$fw.log" -Pattern ': error ' | Select-Object -ExpandProperty Line -Unique) | Select-Object -First 20
    if ($build -ne 0) { continue }
    $dll = "$worktree\tests\CircleAI.Tests\bin\Release\$fw\CircleAI.Tests.dll"
    dotnet test $dll --filter 'FullyQualifiedName~CircleAI' --logger 'console;verbosity=normal' 2>&1 | Set-Content -Path "$scratch\s8-full-test-$fw.log" -Encoding utf8
    "   test exit $LASTEXITCODE ($([int]((Get-Date) - $t0).TotalSeconds) s)"
    @(Select-String -Path "$scratch\s8-full-test-$fw.log" -Pattern 'Passed!|Failed!|Total tests|Test Run Aborted|^\s+Failed ') | Select-Object -ExpandProperty Line | Select-Object -First 40
}
