# Run inside locked-build.ps1 -Command, from the worktree: (optionally) regenerate the runtime catalogue
# and check it, build the test project (Release, the lock script's own flags) and run the given tests.
#   -Full     the engine's parity and MTP tests at full length (12 x 32, 4 MTP prompts), detailed output
#   -NoBuild  reuse the last build of that framework
#   -Tag      names the logs: <Tag>-build-<fw>.log, <Tag>-test-<fw>.log in the scratchpad
param([string]$Filter, [string]$Framework = 'net10.0', [switch]$NoSync, [switch]$NoBuild, [switch]$Full, [string]$Tag = 's5')
$ErrorActionPreference = 'Continue'
$msb = "C:\Program Files\Microsoft Visual Studio\18\Insiders\MSBuild\Current\Bin\amd64\MSBuild.exe"
$scratch = "."
$repo = (Get-Location).Path
$env:RestoreIgnoreFailedSources = 'true'
if (-not $NoSync) {
    dotnet run --project tools/sync-registry -p:UseSharedCompilation=false 2>&1 | ForEach-Object { "$_" } | Where-Object { $_ -match 'repin|wrote|DRIFT|error' }
    dotnet run --project tools/sync-registry -p:UseSharedCompilation=false -- --check 2>&1 | ForEach-Object { "$_" } | Select-Object -Last 2
}
$proj = "$repo\tests\CircleAI.Tests\CircleAI.Tests.csproj"
if (-not $NoBuild) {
    & $msb $proj -t:Restore -low -m:1 -nodeReuse:false -p:Configuration=Release -v:m -nologo 2>&1 | Out-Null
    $buildLog = Join-Path $scratch "$Tag-build-$Framework.log"
    $t0 = Get-Date
    & $msb $proj -low -m:1 -nodeReuse:false -p:UseSharedCompilation=false -p:Configuration=Release "-p:TargetFramework=$Framework" -v:m -nologo 2>&1 | Set-Content -Path $buildLog -Encoding utf8
    "[$Framework] build exit $LASTEXITCODE in $([int]((Get-Date) - $t0).TotalSeconds) s"
    @(Select-String -Path $buildLog -Pattern ': error ' | Select-Object -ExpandProperty Line -Unique) | Select-Object -First 20
}
if ($Full) {
    $env:BONSAI2_PARITY_CASES = '12'; $env:BONSAI2_PARITY_STEPS = '32'; $env:BONSAI2_MTP_CASES = '4'; $verbosity = 'detailed'
} else {
    Remove-Item Env:BONSAI2_PARITY_CASES, Env:BONSAI2_PARITY_STEPS, Env:BONSAI2_MTP_CASES -ErrorAction SilentlyContinue; $verbosity = 'normal'
}
$testLog = Join-Path $scratch "$Tag-test-$Framework.log"
$t0 = Get-Date
dotnet test "$repo\tests\CircleAI.Tests\bin\Release\$Framework\CircleAI.Tests.dll" --filter $Filter --logger "console;verbosity=$verbosity" 2>&1 | Set-Content -Path $testLog -Encoding utf8
"[$Framework] test exit $LASTEXITCODE in $([int]((Get-Date) - $t0).TotalSeconds) s (log $testLog)"
$pattern = if ($Full) { 'Total tests|Passed:|Failed:|Skipped:|Test Run Aborted|^\s+Failed |Error Message|differ by more|tolerance |largest top-20|top-20 differences|DIFFERENT TOP|torn|drafts kept|tok/s|SKIPPED' } else { 'Total tests|Passed:|Failed:|Skipped:|Test Run Aborted|^\s+Failed ' }
@(Select-String -Path $testLog -Pattern $pattern | Select-Object -ExpandProperty Line -Unique) | Select-Object -First 80
