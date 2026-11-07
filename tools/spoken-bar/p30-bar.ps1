# Plays a plan of recordings through the whole spoken turn, each as somebody would: scene after scene, a conversation kept together,
# each new one fresh. Reads only: no setting changed, the screen left as it is. A kill by the phone is noted and the plan carries on.
#
# THE LOG IS THE INSTRUMENT, AND IT STOPPED. A stream over adb into a file here went quiet twice on 2026-11-07, its process alive and
# the phone logging happily, and turns that finished normally were called hung. So the phone keeps its own log file; this asks it
# for just the lines that matter, and only once the clip has had time to play, so the part of a turn being timed is left alone.
param([string]$Serial = 'UTKDU19919000815', [string]$Plan, [string]$Name = 'bar-run', [int]$Rest = 1, [int]$MostPerTurn = 120)
$ErrorActionPreference = 'Continue'
$scratch = $PSScriptRoot
$adb = 'C:\Program Files (x86)\Android\android-sdk\platform-tools\adb.exe'
$pkg = 'com.bhengubv.circleai.service'
$apk = 'C:\Dev\Solutions\com.bhengubv\CircleAI\src\CircleAIService\bin\Release\net10.0-android\android-arm64\com.bhengubv.circleai.service-Signed.apk'
$out = "$scratch\$Name"
$phoneLog = '/data/local/tmp/soak-log.txt'
New-Item -ItemType Directory -Force $out | Out-Null
$manifest = Get-Content "$scratch\bar\manifest.json" -Raw | ConvertFrom-Json

$sampler = @'
n=$1
i=0
while [ $i -lt $n ]; do
  read a < /sys/devices/system/cpu/cpu0/cpufreq/scaling_cur_freq
  read b < /sys/devices/system/cpu/cpu4/cpufreq/scaling_cur_freq
  echo "$EPOCHREALTIME $a $b"
  sleep 0.2
  i=$((i+1))
done
'@
$probe = @'
pkg=com.bhengubv.circleai.service
svc=$(pidof $pkg)
brain=$(pidof $pkg:inference)
echo "pids $svc $brain"
for p in $svc $brain; do
  echo "mem $p $(grep -E 'VmRSS|VmSwap' /proc/$p/status | tr -s ' \t' ' ' | tr '\n' ' ')"
  echo "stat $p $(sed 's/^.*) //' /proc/$p/stat | cut -d' ' -f10,12,13)"
done
grep -E 'MemAvailable|SwapFree' /proc/meminfo | tr -s ' '
dumpsys thermalservice | sed -n '/Current temperatures/,$p' | grep -E 'Temperature\{|CoolingDevice\{'
dumpsys battery | grep -E ' level:| temperature:'
'@
foreach ($file in @{ 'soak-clocks.sh' = $sampler; 'soak-probe.sh' = $probe }.GetEnumerator()) {
    [IO.File]::WriteAllText("$out\$($file.Key)", $file.Value.Replace("`r`n", "`n"))
    & $adb -s $Serial push "$out\$($file.Key)" "/data/local/tmp/$($file.Key)" 2>$null | Out-Null
}

$steps = @(Get-Content $Plan | Where-Object { $_.Trim() })
$samples = $steps.Count * 40 * 5 + 900
$logPid = "$(& $adb -s $Serial shell "rm -f $phoneLog; nohup logcat -v threadtime -f $phoneLog > /dev/null 2>&1 & echo `$!")".Trim()
$clockPid = "$(& $adb -s $Serial shell "sh /data/local/tmp/soak-clocks.sh $samples > /data/local/tmp/soak-clocks.txt 2>&1 & echo `$!")".Trim()
"phone-side log $logPid, clocks $clockPid"
Start-Sleep -Seconds 2

function Offset { [long]("$(& $adb -s $Serial shell "wc -c < $phoneLog" 2>$null)".Trim()) }
function Since([long]$from, [string]$pattern) { @(& $adb -s $Serial shell "tail -c +$($from + 1) $phoneLog | grep -a -E '$pattern' | cut -c1-260" 2>$null) }
$killed = 'Killing [0-9]+:com.bhengubv.circleai.*iAwareK'

function BackFrom([long]$from) {
    $until = (Get-Date).AddSeconds(280)
    do { Start-Sleep -Seconds 3; $lines = Since $from 'greeting warmed|greeting could not' } until ($lines.Count -gt 0 -or (Get-Date) -gt $until)
    "    back: $(if ($lines.Count) { ($lines[0] -replace '^.*(greeting)', '$1') } else { 'NO GREETING' })"
    Start-Sleep -Seconds 25
}

"######## started $(Get-Date -Format HH:mm:ss), phone $(& $adb -s $Serial shell "date '+%H:%M:%S'"), $($steps.Count) turns"
$n = 0
$missed = 0
foreach ($step in $steps) {
    $parts = $step.Trim() -split '\s+'
    $clip = $parts[0]
    $fresh = if ($parts[1] -eq '1') { '--ez fresh true' } else { '' }
    $n++
    $front = "$(& $adb -s $Serial shell "dumpsys window | grep mCurrentFocus" 2>$null)"
    if ($front -notmatch 'com\.huawei\.android\.launcher/|com\.bhengubv\.circleai\.hybrid/|StatusBar|mCurrentFocus=null') { "SOMETHING ELSE IS IN FRONT - STOPPING: $front"; break }

    $from = Offset
    $sent = "$(& $adb -s $Serial shell "date '+%H:%M:%S'" 2>$null)".Trim()
    & $adb -s $Serial shell "am broadcast -n $pkg/ai.circle.SoakReceiver -a com.bhengubv.circleai.action.SOAK --es do talk --es file bar/$clip.wav $fresh" 2>$null | Out-Null

    # THE CLIP HAS TO PLAY AND THE GREETING COME FIRST: nothing is asked of the phone until that and a few seconds more have passed.
    Start-Sleep -Milliseconds ([int]$manifest.$clip.total + 7000)
    $deadline = (Get-Date).AddSeconds($MostPerTurn)
    do {
        $lines = Since $from "CircleAI.Soak: talk: |$killed"
        $over = @($lines | Where-Object { $_ -match 'CircleAI.Soak: talk: ' }).Count -gt 0
        $dead = @($lines | Where-Object { $_ -match 'iAwareK' })
        if (-not $over -and $dead.Count -eq 0) { Start-Sleep -Milliseconds 1500 }
    } until ($over -or $dead.Count -gt 0 -or (Get-Date) -gt $deadline)

    $turn = @(Since $from 'spoken turn:') | Select-Object -First 1
    $short = 'no turn line'
    if ($turn -match 'spoken turn: (\d+) ms.*ears (-?\d+), brain (\d+), to the voice (\d+), voice (\d+).*answer (\d+) ms') {
        $short = "turn $($Matches[1]) ms (ears $($Matches[2]), brain $($Matches[3]), to voice $($Matches[4]), voice $($Matches[5])), gap $($Matches[6])"
    }
    elseif ($turn -match 'no sound') { $short = 'NO SOUND CAME OUT' }
    "{0,3} {1} {2,-6} {3}{4}" -f $n, $sent, $clip, $short, $(if ($over) { '' } elseif ($dead.Count) { ' KILLED IN THE TURN' } else { ' NEVER ENDED' })
    Add-Content "$out\turns.txt" "$n $sent $clip"
    Add-Content "$out\probe.txt" "=== $n $clip $sent"
    $clock = [Diagnostics.Stopwatch]::StartNew()
    & $adb -s $Serial shell "sh /data/local/tmp/soak-probe.sh" 2>&1 | Add-Content "$out\probe.txt"
    if ($clock.ElapsedMilliseconds -gt 5000) { "    the probe took $($clock.ElapsedMilliseconds) ms" }

    # A kill between turns shows here too.
    $dead = @(Since $from $killed)
    if ($dead.Count) {
        $dead | ForEach-Object { "    KILLED: $($_ -replace '^.*(Killing)', '$1')"; Add-Content "$out\kills.txt" "$sent $_" }
        BackFrom $from
    }
    elseif (@(Since $from 'render said nothing').Count) {
        # THE VOICE GAVE UP AND STAYS GIVEN UP until the process goes. Counted, then started again so the turns after it can be timed.
        "    MUTED by this turn: starting the service again"
        Add-Content "$out\mutes.txt" "$n $sent $clip"
        $from = Offset
        & $adb -s $Serial install -r --no-incremental $apk 2>&1 | Where-Object { $_ -match 'Success|Failure' }
        BackFrom $from
    }

    if ($over -or $dead.Count) { $missed = 0 } else { $missed++ }
    if ($missed -ge 2) { "TWO TURNS NEVER ENDED - STOPPING"; break }
    Start-Sleep -Seconds $Rest
}

"######## turns over $(Get-Date -Format HH:mm:ss), $n asked"
& $adb -s $Serial shell "kill $clockPid $logPid" 2>$null
& $adb -s $Serial pull $phoneLog "$out\logcat1.txt" 2>&1 | Select-Object -Last 1
& $adb -s $Serial pull /data/local/tmp/soak-clocks.txt "$out\clocks.txt" 2>$null | Out-Null
& $adb -s $Serial shell "rm -f $phoneLog /data/local/tmp/soak-clocks.sh /data/local/tmp/soak-clocks.txt /data/local/tmp/soak-probe.sh" 2>$null
"######## finished $(Get-Date -Format HH:mm:ss)"
