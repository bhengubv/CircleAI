# S8 vision: build the test project (net10.0 Release) and run the vision tests; when they pass, the
# whole suite on net10.0 and then net9.0 (s8-full.ps1) in the same lock. Locked by the caller.
$ErrorActionPreference = 'Continue'
$worktree = 'C:\Dev\Solutions\com.bhengubv\CircleAI\.claude\worktrees\agent-a5582f0c55c8a4162'
$scratch = '.'
$msb = 'C:\Program Files\Microsoft Visual Studio\18\Insiders\MSBuild\Current\Bin\amd64\MSBuild.exe'
[System.Diagnostics.Process]::GetCurrentProcess().PriorityClass = 'BelowNormal'
$t0 = Get-Date
$proj = "$worktree\tests\CircleAI.Tests\CircleAI.Tests.csproj"
& $msb $proj -low -m:1 -nodeReuse:false -p:UseSharedCompilation=false -p:Configuration=Release -p:TargetFramework=net10.0 -v:m -nologo 2>&1 |
    Set-Content -Path "$scratch\s8-build.log" -Encoding utf8
$build = $LASTEXITCODE
"build exit $build ($([int]((Get-Date) - $t0).TotalSeconds) s)"
@(Select-String -Path "$scratch\s8-build.log" -Pattern ': error ' | Select-Object -ExpandProperty Line -Unique) | Select-Object -First 30
if ($build -ne 0) { exit 1 }

$dll = "$worktree\tests\CircleAI.Tests\bin\Release\net10.0\CircleAI.Tests.dll"
$filter = 'FullyQualifiedName~ManagedVisionTests|FullyQualifiedName~WeightMatrixTests|FullyQualifiedName~VisionImageTests|FullyQualifiedName~VisionTowerDiscoveryTests|FullyQualifiedName~ManagedChatGeneratorTests.System_messages'
dotnet test $dll --filter $filter --logger 'console;verbosity=detailed' 2>&1 | Set-Content -Path "$scratch\s8-test.log" -Encoding utf8
$vision = $LASTEXITCODE
"vision test exit $vision ($([int]((Get-Date) - $t0).TotalSeconds) s)"
@(Select-String -Path "$scratch\s8-test.log" -Pattern 'Passed |Failed |Total tests|Passed!|Failed!|Aborted') | Select-Object -ExpandProperty Line | Select-Object -First 50
if ($vision -ne 0) { exit 1 }

& "$scratch\s8-full.ps1"
