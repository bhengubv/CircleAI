# Light Release build of CircleAI.Tests for one framework, then the tests a filter names.
# Refuses to build after a disk stall in the last 15 minutes. The whole build output goes to a
# log first (cutting the pipeline short ends MSBuild), then its errors are picked out.
param([string]$Framework = 'net10.0', [string]$Filter = 'FullyQualifiedName~ManagedEngine', [switch]$SkipBuild)
$ErrorActionPreference = 'Continue'
$msb = "C:\Program Files\Microsoft Visual Studio\18\Insiders\MSBuild\Current\Bin\amd64\MSBuild.exe"
$repo = "C:\Dev\Solutions\com.bhengubv\CircleAI"
$proj = "$repo\tests\CircleAI.Tests\CircleAI.Tests.csproj"
$dll = "$repo\tests\CircleAI.Tests\bin\Release\$Framework\CircleAI.Tests.dll"
$scratch = "."
$buildLog = "$scratch\test-build-$Framework.log"
$testLog = "$scratch\test-run-$Framework.log"

if (-not $SkipBuild) {
    $e = Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'ESENT'; Id = 508, 510; StartTime = (Get-Date).AddMinutes(-15) } -ErrorAction SilentlyContinue
    if (@($e).Count -gt 0) { "STALLS: $(@($e).Count) - not building"; exit 1 }
    $start = Get-Date
    & $msb $proj -low -m:1 -nodeReuse:false -p:UseSharedCompilation=false -p:Configuration=Release "-p:TargetFramework=$Framework" -v:m -nologo 2>&1 |
        Set-Content -Path $buildLog -Encoding utf8
    $code = $LASTEXITCODE
    "build exit $code in $([int]((Get-Date) - $start).TotalSeconds) s"
    @(Select-String -Path $buildLog -Pattern ': error ' | Select-Object -ExpandProperty Line -Unique) | Select-Object -First 25
    $stalls = @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'ESENT'; Id = 508, 510; StartTime = $start } -ErrorAction SilentlyContinue)
    "disk stalls during the build: $($stalls.Count)"
    if ($code -ne 0) { exit 1 }
}

$start = Get-Date
dotnet test $dll --filter $Filter --logger "console;verbosity=normal" 2>&1 | Set-Content -Path $testLog -Encoding utf8
$code = $LASTEXITCODE
@(Select-String -Path $testLog -Pattern 'Passed!|Failed!|^\s+Failed |Total tests|Aborted|SKIPPED|\[FAIL\]' | Select-Object -ExpandProperty Line) | Select-Object -First 60
"test exit $code in $([int]((Get-Date) - $start).TotalSeconds) s"
