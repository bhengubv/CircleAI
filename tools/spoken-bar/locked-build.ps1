# ONE BUILD AT A TIME ON THIS PC, WHOEVER ASKS - the agents in their own worktrees and the main session.
# The C: drive is an SMR hard disk that freezes under two builds at once (the owner's never-hog rule), so every
# build and test run goes through here: it waits for an exclusive lock (held as an open file handle, so a crashed
# holder releases it), refuses after a disk stall, builds light, runs what was asked, and lets go.
#
#   Tests:    -Repo <worktree root> -Framework net10.0 -Filter "FullyQualifiedName~MyTests"
#   All:      -Repo <worktree root> -Framework net9.0 -Filter "FullyQualifiedName~CircleAI"
#   Android:  -Repo <worktree root> -Android        (compile-only: CircleAI + the service + the app, Debug)
#   Other:    -Repo <worktree root> -Command "<a PowerShell command line>"   (anything heavy - a reference run,
#             a tool's dotnet run, a download - run inside the repo folder while holding the same lock)
#
# Run it in the BACKGROUND (it may wait a long time for the lock) and read its output when notified.
param(
    [Parameter(Mandatory)][string]$Repo,
    [string]$Framework = 'net10.0',
    [string]$Filter = '',
    [switch]$Android,
    [string]$Command = '',
    [int]$WaitMinutes = 180
)
$ErrorActionPreference = 'Continue'
$msb = "C:\Program Files\Microsoft Visual Studio\18\Insiders\MSBuild\Current\Bin\amd64\MSBuild.exe"
$scratch = $PSScriptRoot
$lockPath = Join-Path $scratch 'build.lock'
$tag = (Split-Path $Repo -Leaf) + '-' + (Get-Date -Format 'HHmmss')
$logDir = Join-Path $scratch 'locked-logs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null

# FIRST COME, FIRST SERVED. Every waiter takes a ticket (named by when it arrived and its process id) and
# only the oldest live ticket may take the lock - a waiter that polls in a free-for-all can lose every race
# for an hour. A ticket whose process has gone (or whose id now belongs to a newer process) is cleared.
$queueDir = Join-Path $scratch 'build-queue'
New-Item -ItemType Directory -Force -Path $queueDir | Out-Null
$arrived = [DateTime]::UtcNow.Ticks
$ticket = Join-Path $queueDir ('{0:D20}-{1}.ticket' -f $arrived, $PID)
Set-Content -Path $ticket -Value $Repo

function Get-FirstLiveTicket {
    foreach ($t in @(Get-ChildItem $queueDir -Filter *.ticket | Sort-Object Name)) {
        $parts = $t.BaseName -split '-'
        $alive = $true
        try {
            $owner = Get-Process -Id ([int]$parts[1]) -ErrorAction Stop
            if ($owner.StartTime.ToUniversalTime().Ticks -gt [long]$parts[0]) { $alive = $false }  # the id was reused
        }
        catch [Microsoft.PowerShell.Commands.ProcessCommandException] { $alive = $false }      # no such process
        catch { }                                                                             # unreadable: assume alive
        if ($alive) { return $t.FullName }
        Remove-Item $t.FullName -ErrorAction SilentlyContinue
    }
    return $null
}

$deadline = (Get-Date).AddMinutes($WaitMinutes)
$lock = $null
while (-not $lock) {
    if ((Get-FirstLiveTicket) -eq $ticket) {
        try { $lock = [System.IO.File]::Open($lockPath, 'OpenOrCreate', 'ReadWrite', 'None') } catch { }
    }
    if (-not $lock) {
        if ((Get-Date) -gt $deadline) { Remove-Item $ticket -ErrorAction SilentlyContinue; "GAVE UP waiting $WaitMinutes min for the build lock"; exit 2 }
        Start-Sleep -Seconds 15
    }
}
"lock taken at $(Get-Date -Format 'HH:mm:ss') for $Repo"
try {
    $e = Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'ESENT'; Id = 508, 510; StartTime = (Get-Date).AddMinutes(-15) } -ErrorAction SilentlyContinue
    if (@($e).Count -gt 0) { "DISK STALLS in the last 15 min: $(@($e).Count) - not building; try again later"; exit 3 }
    $start = Get-Date

    if ($Command) {
        $log = Join-Path $logDir "$tag-command.log"
        Push-Location $Repo
        try { Invoke-Expression $Command 2>&1 | Tee-Object -FilePath $log | Select-Object -Last 40 }
        finally { Pop-Location }
        "command done in $([int]((Get-Date) - $start).TotalSeconds) s (full log $log)"
        exit 0
    }

    if ($Android) {
        foreach ($p in @("$Repo\src\CircleAIService\CircleAIService.csproj", "$Repo\samples\CircleAI.Samples.Hybrid\CircleAI.Samples.App\CircleAI.Samples.App.csproj")) {
            $log = Join-Path $logDir "$tag-android-$([System.IO.Path]::GetFileNameWithoutExtension($p)).log"
            & $msb $p -t:Restore -low -m:1 -nodeReuse:false -p:Configuration=Debug -v:m -nologo 2>&1 | Set-Content -Path "$log.restore" -Encoding utf8
            & $msb $p -t:Compile -low -m:1 -nodeReuse:false -p:UseSharedCompilation=false -p:Configuration=Debug -p:TargetFramework=net10.0-android -v:m -nologo 2>&1 | Set-Content -Path $log -Encoding utf8
            $code = $LASTEXITCODE
            "$(Split-Path $p -Leaf) android compile exit $code (log $log)"
            @(Select-String -Path $log -Pattern ': error ' | Select-Object -ExpandProperty Line -Unique) | Select-Object -First 25
            if ($code -ne 0) { exit 1 }
        }
        "android compile done in $([int]((Get-Date) - $start).TotalSeconds) s"
        exit 0
    }

    $proj = "$Repo\tests\CircleAI.Tests\CircleAI.Tests.csproj"
    $buildLog = Join-Path $logDir "$tag-build-$Framework.log"
    # RESTORE ON ITS OWN - never -restore beside -p:TargetFramework (the house rule).
    & $msb $proj -t:Restore -low -m:1 -nodeReuse:false -p:Configuration=Release -v:m -nologo 2>&1 | Set-Content -Path "$buildLog.restore" -Encoding utf8
    & $msb $proj -low -m:1 -nodeReuse:false -p:UseSharedCompilation=false -p:Configuration=Release "-p:TargetFramework=$Framework" -v:m -nologo 2>&1 |
        Set-Content -Path $buildLog -Encoding utf8
    $code = $LASTEXITCODE
    "build exit $code in $([int]((Get-Date) - $start).TotalSeconds) s (log $buildLog)"
    @(Select-String -Path $buildLog -Pattern ': error ' | Select-Object -ExpandProperty Line -Unique) | Select-Object -First 25
    if ($code -ne 0) { exit 1 }
    if (-not $Filter) { exit 0 }

    $dll = "$Repo\tests\CircleAI.Tests\bin\Release\$Framework\CircleAI.Tests.dll"
    $testLog = Join-Path $logDir "$tag-test-$Framework.log"
    $t0 = Get-Date
    dotnet test $dll --filter $Filter --logger "console;verbosity=normal" 2>&1 | Set-Content -Path $testLog -Encoding utf8
    $code = $LASTEXITCODE
    @(Select-String -Path $testLog -Pattern 'Passed!|Failed!|^\s+Failed |Total tests|Test Run Aborted|\[FAIL\]' | Select-Object -ExpandProperty Line) | Select-Object -First 40
    "test exit $code in $([int]((Get-Date) - $t0).TotalSeconds) s (log $testLog)"
    exit $code
}
finally {
    $lock.Dispose()
    Remove-Item $ticket -ErrorAction SilentlyContinue
    "lock released at $(Get-Date -Format 'HH:mm:ss')"
}
