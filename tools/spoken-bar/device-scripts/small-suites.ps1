# The fourteen smaller suites on every leg they target, and the bUnit suite - built light with
# Visual Studio's MSBuild (restore on its own), run one project at a time, each count read from its
# own run (a summary that says "Passed!" can hide an abort). Run under locked-build.ps1 -Command.
# A disk stall since the last project pauses it five minutes; four stalls in a row stop it.
$ErrorActionPreference = 'Continue'
$msb = "C:\Program Files\Microsoft Visual Studio\18\Insiders\MSBuild\Current\Bin\amd64\MSBuild.exe"
$repo = "C:\Dev\Solutions\com.bhengubv\CircleAI"
$scratch = "."
$logDir = Join-Path $scratch 'small-suites'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null

$suites = @(
    'CircleAI.Agents.Peer.Tests', 'CircleAI.Companion.Tests', 'CircleAI.Federation.Tests',
    'CircleAI.Hosting.InferenceBridge.Tests', 'CircleAI.Inference.Server.Tests', 'CircleAI.Knowledge.Tests',
    'CircleAI.Memory.Tests', 'CircleAI.Orchestration.Tests', 'CircleAI.Personal.Tests',
    'CircleAI.Personality.Tests', 'CircleAI.Runtime.Tests', 'CircleAI.Security.Tests',
    'CircleAI.Simulation.Tests', 'CircleAI.Wearable.Biosignals.Tests', 'CircleAI.Samples.Ui.Tests')

$script:since = Get-Date
function Wait-ForDisk {
    for ($i = 0; $i -lt 4; $i++) {
        $e = @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'ESENT'; Id = 508, 510; StartTime = $script:since } -ErrorAction SilentlyContinue)
        $script:since = Get-Date
        if ($e.Count -eq 0) { return $true }
        "[$(Get-Date -Format HH:mm:ss)] disk stall ($($e.Count)) - pausing 5 min"
        Start-Sleep -Seconds 300
    }
    return $false
}

function Count([string]$log, [string]$label) {
    $m = Select-String -Path $log -Pattern "^\s*${label}:\s*(\d+)" | Select-Object -Last 1
    if ($m) { [int]$m.Matches[0].Groups[1].Value } else { -1 }
}

$summary = @()
foreach ($n in $suites) {
    if (-not (Wait-ForDisk)) { $summary += "STOPPED before $n - the disk kept stalling"; break }
    $proj = "$repo\tests\$n\$n.csproj"
    $tfms = @(((Select-String -Path $proj -Pattern '<TargetFrameworks?>([^<]*)' | Select-Object -First 1).Matches[0].Groups[1].Value -split ';') | Where-Object { $_ })
    $blog = Join-Path $logDir "$n-build.log"
    $t0 = Get-Date
    & $msb $proj -t:Restore -low -m:1 -nodeReuse:false -p:Configuration=Release -v:m -nologo 2>&1 | Set-Content -Path "$blog.restore" -Encoding utf8
    & $msb $proj -low -m:1 -nodeReuse:false -p:UseSharedCompilation=false -p:Configuration=Release -v:m -nologo 2>&1 | Set-Content -Path $blog -Encoding utf8
    $code = $LASTEXITCODE
    "[$(Get-Date -Format HH:mm:ss)] $n built ($($tfms -join ', ')) exit $code in $([int]((Get-Date) - $t0).TotalSeconds) s"
    if ($code -ne 0) {
        $summary += "$n BUILD FAILED (exit $code, log $blog)"
        @(Select-String -Path $blog -Pattern ': error ' | Select-Object -ExpandProperty Line -Unique) | Select-Object -First 10
        continue
    }
    foreach ($tf in $tfms) {
        $dll = "$repo\tests\$n\bin\Release\$tf\$n.dll"
        if (-not (Test-Path $dll)) { $summary += "$n $tf NO DLL at $dll"; continue }
        $tlog = Join-Path $logDir "$n-$tf.log"
        dotnet test $dll --logger "console;verbosity=normal" 2>&1 | Set-Content -Path $tlog -Encoding utf8
        $tcode = $LASTEXITCODE
        $total = Count $tlog 'Total tests'; $passed = Count $tlog 'Passed'; $failed = Count $tlog 'Failed'
        $aborted = @(Select-String -Path $tlog -Pattern 'Test Run Aborted').Count -gt 0
        $line = "$n ${tf}: $passed of $total passed, $failed failed, exit $tcode$(if ($aborted) { ' - ABORTED' })"
        $summary += $line
        $line
        if ($failed -gt 0) { @(Select-String -Path $tlog -Pattern '^\s+Failed ' | Select-Object -ExpandProperty Line) | Select-Object -First 10 }
    }
    Start-Sleep -Seconds 20
}
"=== SUMMARY $(Get-Date -Format HH:mm:ss)"
$summary
