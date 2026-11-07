# S7: compile the heads the test legs do not build - the service and the sample app for android, and the
# service's desktop head (Host/Program.cs) - in one hold of the build lock. Run through locked-build.ps1 -Command.
param([string]$Repo, [string]$Msb)
$ErrorActionPreference = 'Continue'
$failed = $false
foreach ($p in @("$Repo\src\CircleAIService\CircleAIService.csproj",
                 "$Repo\samples\CircleAI.Samples.Hybrid\CircleAI.Samples.App\CircleAI.Samples.App.csproj")) {
    & $Msb $p -t:Restore -low -m:1 -nodeReuse:false -p:Configuration=Debug -v:m -nologo 2>&1 | Out-Null
    $out = & $Msb $p -t:Compile -low -m:1 -nodeReuse:false -p:UseSharedCompilation=false -p:Configuration=Debug -p:TargetFramework=net10.0-android -v:m -nologo 2>&1
    $code = $LASTEXITCODE
    "$(Split-Path $p -Leaf) net10.0-android compile exit $code"
    @($out | Select-String ': error ' | ForEach-Object { $_.Line } | Select-Object -Unique) | Select-Object -First 25
    if ($code -ne 0) { $failed = $true }
}
$out = & $Msb "$Repo\src\CircleAIService\CircleAIService.csproj" -t:Compile -low -m:1 -nodeReuse:false -p:UseSharedCompilation=false -p:Configuration=Debug -p:TargetFramework=net10.0 -v:m -nologo 2>&1
$code = $LASTEXITCODE
"CircleAIService.csproj net10.0 (desktop head) compile exit $code"
@($out | Select-String ': error ' | ForEach-Object { $_.Line } | Select-Object -Unique) | Select-Object -First 25
if ($code -ne 0) { $failed = $true }
if ($failed) { "HEADS: FAILED" } else { "HEADS: ALL COMPILED" }
