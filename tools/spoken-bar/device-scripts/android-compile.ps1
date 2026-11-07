# Compile-only check of the Android heads (no packaging, no install): the service and the app,
# each restored first on its own (never -restore with -p:TargetFramework=), Debug, one at a time,
# light, with a disk-stall check before each. Whole output to a log; errors picked out after.
$ErrorActionPreference = 'Continue'
$msb = "C:\Program Files\Microsoft Visual Studio\18\Insiders\MSBuild\Current\Bin\amd64\MSBuild.exe"
$repo = "C:\Dev\Solutions\com.bhengubv\CircleAI"
$scratch = "."
$projects = @(
    @{ Name = 'service'; Path = "$repo\src\CircleAIService\CircleAIService.csproj" },
    @{ Name = 'app';     Path = "$repo\samples\CircleAI.Samples.Hybrid\CircleAI.Samples.App\CircleAI.Samples.App.csproj" }
)
foreach ($p in $projects) {
    $e = Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'ESENT'; Id = 508, 510; StartTime = (Get-Date).AddMinutes(-15) } -ErrorAction SilentlyContinue
    if (@($e).Count -gt 0) { "STALLS: $(@($e).Count) - stopping before $($p.Name)"; exit 1 }
    $log = "$scratch\android-$($p.Name).log"
    $start = Get-Date
    & $msb $p.Path -t:Restore -low -m:1 -nodeReuse:false -p:Configuration=Debug -v:m -nologo 2>&1 | Set-Content -Path "$log.restore" -Encoding utf8
    "$($p.Name) restore exit $LASTEXITCODE"
    & $msb $p.Path -t:Compile -low -m:1 -nodeReuse:false -p:UseSharedCompilation=false -p:Configuration=Debug -p:TargetFramework=net10.0-android -v:m -nologo 2>&1 | Set-Content -Path $log -Encoding utf8
    $code = $LASTEXITCODE
    "$($p.Name) compile exit $code in $([int]((Get-Date) - $start).TotalSeconds) s"
    @(Select-String -Path $log -Pattern ': error ' | Select-Object -ExpandProperty Line -Unique) | Select-Object -First 20
    if ($code -ne 0) { exit 1 }
}
& dotnet build-server shutdown 2>&1 | Out-Null
"done"
