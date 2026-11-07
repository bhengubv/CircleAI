# Recorded questions one straight after the other through the whole spoken turn, for as long as asked. Reads only: no setting
# changed, the screen left as it is. Everything lands in one folder; p30-soak-read.py reads it afterwards.
param([string]$Serial = 'UTKDU19919000815', [double]$Minutes = 30, [string[]]$Clips = @('q-bird', 'q-sky', 'q-rain', 'q-kenya'), [int]$Rest = 5, [int]$MostPerTurn = 120, [string]$Name = 'soak30')
$ErrorActionPreference = 'Continue'
$scratch = '.'
$adb = 'C:\Program Files (x86)\Android\android-sdk\platform-tools\adb.exe'
$pkg = 'com.bhengubv.circleai.service'
$out = "$scratch\$Name"
New-Item -ItemType Directory -Force $out | Out-Null

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
  echo "cpuset $p $(cat /proc/$p/cpuset)"
done
grep -E 'MemAvailable|SwapFree' /proc/meminfo | tr -s ' '
dumpsys power | grep -E 'Display Power: state'
dumpsys thermalservice | sed -n '/Current temperatures/,$p' | grep -E 'Temperature\{|CoolingDevice\{'
dumpsys thermalservice | grep -E 'Thermal Status'
dumpsys battery | grep -E ' level:| temperature:| status:'
'@
foreach ($file in @{ 'soak-clocks.sh' = $sampler; 'soak-probe.sh' = $probe }.GetEnumerator()) {
    [IO.File]::WriteAllText("$out\$($file.Key)", $file.Value.Replace("`r`n", "`n"))
    & $adb -s $Serial push "$out\$($file.Key)" "/data/local/tmp/$($file.Key)" 2>$null | Out-Null
}
"on the phone: $(& $adb -s $Serial shell "wc -l /data/local/tmp/soak-clocks.sh /data/local/tmp/soak-probe.sh | tail -1")"

$logFile = "$out\logcat.txt"
$stream = Start-Process -FilePath $adb -ArgumentList "-s $Serial logcat -v threadtime -T 1" -RedirectStandardOutput $logFile -RedirectStandardError "$out\logcat-err.txt" -NoNewWindow -PassThru
Start-Sleep -Seconds 2
$reader = [IO.StreamReader]::new([IO.FileStream]::new($logFile, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
$samples = [int]($Minutes * 60 * 5 + 900)
& $adb -s $Serial shell "sh /data/local/tmp/soak-clocks.sh $samples > /data/local/tmp/soak-clocks.txt 2>&1 &"

# The phone can kill the service: note it, wait for it to come back, carry on. The restart is a result, not the end of the run.
function BackAgain([string]$text) {
    $text | Select-String -Pattern 'Killing \d+:com\.bhengubv\.circleai.*' -AllMatches -AllMatches | ForEach-Object { $_.Matches } | ForEach-Object {
        "    KILLED: $($_.Value)"
        Add-Content "$out\kills.txt" $_.Value
    }
    $until = (Get-Date).AddSeconds(240)
    while ($text -notmatch 'greeting warmed|greeting could not' -and (Get-Date) -lt $until) { Start-Sleep -Seconds 2; $text += $reader.ReadToEnd() }
    "    back at $(& $adb -s $Serial shell "date '+%H:%M:%S'" 2>$null): $(if ($text -match 'greeting warmed in (\d+) ms') { "greeting warmed in $($Matches[1]) ms" } else { 'NO GREETING' })"
    Start-Sleep -Seconds 25
}
$killLine = 'Killing \d+:com\.bhengubv\.circleai'

"######## started $(Get-Date -Format HH:mm:ss), phone $(& $adb -s $Serial shell "date '+%H:%M:%S'"), for $Minutes minutes"
$end = (Get-Date).AddMinutes($Minutes)
$n = 0
$missed = 0
while ((Get-Date) -lt $end) {
    $clip = $Clips[$n % $Clips.Count]
    $n++
    $front = "$(& $adb -s $Serial shell "dumpsys window | grep mCurrentFocus" 2>$null)"
    if ($front -notmatch 'com\.huawei\.android\.launcher/|com\.bhengubv\.circleai\.hybrid/|StatusBar|mCurrentFocus=null') { "SOMETHING ELSE IS IN FRONT - STOPPING: $front"; break }
    if ($stream.HasExited) { "THE LOG STREAM STOPPED - STOPPING"; break }

    $sent = "$(& $adb -s $Serial shell "date '+%H:%M:%S'" 2>$null)".Trim()
    $between = $reader.ReadToEnd()
    if ($between -match $killLine) { BackAgain $between; $sent = "$(& $adb -s $Serial shell "date '+%H:%M:%S'" 2>$null)".Trim() }
    $seen = ''
    & $adb -s $Serial shell "am broadcast -n $pkg/ai.circle.SoakReceiver -a com.bhengubv.circleai.action.SOAK --es do talk --es file $clip.wav" 2>$null | Out-Null
    $deadline = (Get-Date).AddSeconds($MostPerTurn)
    do {
        Start-Sleep -Milliseconds 500
        $seen += $reader.ReadToEnd()
        $over = $seen -match 'CircleAI\.Soak: talk: '
        $killed = $seen -match $killLine
    } until ($over -or $killed -or (Get-Date) -gt $deadline)

    $turn = @($seen -split "`n" | Where-Object { $_ -match 'spoken turn:' }) | Select-Object -First 1
    $short = 'NO TURN LINE'
    if ($turn -match 'spoken turn: (\d+) ms.*ears (-?\d+), brain (\d+), to the voice (\d+), voice (\d+).*answer (\d+) ms') {
        $short = "turn $($Matches[1]) ms (ears $($Matches[2]), brain $($Matches[3]), to voice $($Matches[4]), voice $($Matches[5])), gap $($Matches[6])"
    }
    "{0,3} {1} {2,-8} {3}{4}" -f $n, $sent, $clip, $short, $(if ($over) { '' } elseif ($killed) { ' KILLED IN THE TURN' } else { ' NEVER ENDED' })
    Add-Content "$out\turns.txt" "$n $sent $clip"
    Add-Content "$out\probe.txt" "=== $n $clip $sent"
    & $adb -s $Serial shell "sh /data/local/tmp/soak-probe.sh" 2>&1 | Add-Content "$out\probe.txt"

    if ($killed) { BackAgain $seen }
    if ($over -or $killed) { $missed = 0 } else { $missed++ }
    if ($missed -ge 2) { "TWO TURNS NEVER ENDED - STOPPING"; break }
    Start-Sleep -Seconds $Rest
}

"######## turns over $(Get-Date -Format HH:mm:ss), $n asked"
foreach ($q in 'What is the capital of Kenya?', 'What is 12 times 12?', 'Why is the sky blue?') {
    $null = $reader.ReadToEnd()
    $seen = ''
    & $adb -s $Serial shell "am broadcast -n $pkg/ai.circle.SoakReceiver -a com.bhengubv.circleai.action.SOAK --es do engine --ei max 48 --es text '$q'" 2>$null | Out-Null
    $deadline = (Get-Date).AddSeconds(90)
    do { Start-Sleep -Seconds 1; $seen += $reader.ReadToEnd() } until ($seen -match 'engine check done' -or (Get-Date) -gt $deadline)
    "Q: $q"
    ($seen -split "`n" | Where-Object { $_ -match 'engine check \(' -and $_ -match 'loaded in' }) | ForEach-Object { "   " + ($_ -replace '^.*engine check', 'engine check').Trim() }
}
& $adb -s $Serial shell "pkill -f soak-clocks.sh" 2>$null
& $adb -s $Serial pull /data/local/tmp/soak-clocks.txt "$out\clocks.txt" 2>$null | Out-Null
& $adb -s $Serial shell "rm -f /data/local/tmp/soak-clocks.sh /data/local/tmp/soak-clocks.txt /data/local/tmp/soak-probe.sh" 2>$null
Start-Sleep -Seconds 2
$reader.Dispose()
Stop-Process -Id $stream.Id -ErrorAction SilentlyContinue
"######## finished $(Get-Date -Format HH:mm:ss); clocks: $((Get-Item "$out\clocks.txt" -ErrorAction SilentlyContinue).Length) bytes; log: $((Get-Item $logFile).Length) bytes"
