# S6's build, run INSIDE locked-build.ps1 -Command (so under the shared lock), from the worktree root.
# The same steps as locked-build.ps1's own build mode, with one difference: the restore ignores the
# missing sibling package source (..\aether-protocol\artifacts\packages does not exist next to a
# worktree), which otherwise fails a fresh restore of the floating 10.* package versions (NU1301).
param(
    [string]$Framework = 'net10.0',
    [string]$Filter = '',
    [switch]$Android
)
$ErrorActionPreference = 'Continue'
$msb = "C:\Program Files\Microsoft Visual Studio\18\Insiders\MSBuild\Current\Bin\amd64\MSBuild.exe"
$logDir = ".\locked-logs"
$tag = 's6-' + (Get-Date -Format 'HHmmss')
$repo = (Get-Location).Path

if ($Android) {
    foreach ($p in @("$repo\src\CircleAIService\CircleAIService.csproj", "$repo\samples\CircleAI.Samples.Hybrid\CircleAI.Samples.App\CircleAI.Samples.App.csproj")) {
        $log = Join-Path $logDir "$tag-android-$([System.IO.Path]::GetFileNameWithoutExtension($p)).log"
        & $msb $p -t:Restore -low -m:1 -nodeReuse:false -p:Configuration=Debug -p:RestoreIgnoreFailedSources=true -v:m -nologo 2>&1 | Set-Content -Path "$log.restore" -Encoding utf8
        & $msb $p -t:Compile -low -m:1 -nodeReuse:false -p:UseSharedCompilation=false -p:Configuration=Debug -p:TargetFramework=net10.0-android -v:m -nologo 2>&1 | Set-Content -Path $log -Encoding utf8
        $code = $LASTEXITCODE
        "$(Split-Path $p -Leaf) android compile exit $code (log $log)"
        @(Select-String -Path $log -Pattern ': error ' | Select-Object -ExpandProperty Line -Unique) | Select-Object -First 25
        if ($code -ne 0) { return }
    }
    "android compile done"
    return
}

$proj = "$repo\tests\CircleAI.Tests\CircleAI.Tests.csproj"
$buildLog = Join-Path $logDir "$tag-build-$Framework.log"
& $msb $proj -t:Restore -low -m:1 -nodeReuse:false -p:Configuration=Release -p:RestoreIgnoreFailedSources=true -v:m -nologo 2>&1 | Set-Content -Path "$buildLog.restore" -Encoding utf8
"restore exit $LASTEXITCODE"
& $msb $proj -low -m:1 -nodeReuse:false -p:UseSharedCompilation=false -p:Configuration=Release "-p:TargetFramework=$Framework" -v:m -nologo 2>&1 |
    Set-Content -Path $buildLog -Encoding utf8
$code = $LASTEXITCODE
"build exit $code (log $buildLog)"
@(Select-String -Path $buildLog -Pattern ': error ' | Select-Object -ExpandProperty Line -Unique) | Select-Object -First 40
if ($code -ne 0 -or -not $Filter) { return }

$dll = "$repo\tests\CircleAI.Tests\bin\Release\$Framework\CircleAI.Tests.dll"
$testLog = Join-Path $logDir "$tag-test-$Framework.log"
dotnet test $dll --filter $Filter --logger "console;verbosity=normal" 2>&1 | Set-Content -Path $testLog -Encoding utf8
$tcode = $LASTEXITCODE
@(Select-String -Path $testLog -Pattern 'Passed!|Failed!|^\s+Failed |Total tests|Test Run Aborted|\[FAIL\]' | Select-Object -ExpandProperty Line) | Select-Object -First 60
"test exit $tcode (log $testLog)"
