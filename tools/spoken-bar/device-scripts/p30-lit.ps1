# Recorded questions, each from a dark screen: screen off (the owner's yes for measuring this case, 2026-11-06), wait, ask, read the turn's
# own timing and what the display and the clocks did. Stops if anything but our app, the launcher or the lock screen is in front.
param([string]$Serial = 'UTKDU19919000815', [string[]]$Clips = @('q-bird','q-sky','q-rain','q-bird','q-sky','q-rain'), [int]$Each = 30, [int]$DarkFor = 8)
$adb = 'C:\Program Files (x86)\Android\android-sdk\platform-tools\adb.exe'
$pkg = 'com.bhengubv.circleai.service'
$sp = '.'
function Display { ("$(& $adb -s $Serial shell "dumpsys power | grep -E 'Display Power: state'")" -replace '.*state=', '').Trim() }

# The clocks and the display, sampled on the phone itself, written to a file there.
$sampler = @'
n=$1
i=0
while [ $i -lt $n ]; do
  echo "$(date +%s.%N | cut -c1-14) c0=$(cat /sys/devices/system/cpu/cpu0/cpufreq/scaling_cur_freq) c4=$(cat /sys/devices/system/cpu/cpu4/cpufreq/scaling_cur_freq)"
  sleep 0.2
  i=$((i+1))
done
'@
$tmp = [IO.Path]::GetTempFileName()
[IO.File]::WriteAllText($tmp, $sampler.Replace("`r`n", "`n"))
& $adb -s $Serial push $tmp /data/local/tmp/clocks.sh 2>$null | Out-Null
Remove-Item $tmp

foreach ($clip in $Clips) {
    $f = "$(& $adb -s $Serial shell "dumpsys window | grep mCurrentFocus")"
    if ($f -notmatch 'com\.huawei\.android\.launcher/|com\.bhengubv\.circleai\.hybrid/|StatusBar') { "SOMETHING ELSE IS IN FRONT - STOPPING: $f"; break }
    & $adb -s $Serial shell "input keyevent KEYCODE_SLEEP"
    Start-Sleep -Seconds $DarkFor
    $before = Display
    $since = "$(& $adb -s $Serial shell "date '+%m-%d %H:%M:%S.000'" 2>$null)".Trim()
    "---- $clip, display before: $before, at $since"
    $job = Start-Job -ScriptBlock { param($a, $s) & $a -s $s shell "sh /data/local/tmp/clocks.sh 60" } -ArgumentList $adb, $Serial
    Start-Sleep -Milliseconds 300
    & $adb -s $Serial shell "am broadcast -n $pkg/ai.circle.SoakReceiver -a com.bhengubv.circleai.action.SOAK --es do talk --es file $clip.wav" 2>$null | Out-Null
    Start-Sleep -Seconds 3
    "display 3 s into the turn: $(Display)"
    $rows = Receive-Job -Job $job -Wait -AutoRemoveJob
    Start-Sleep -Seconds ([Math]::Max(1, $Each - 16))
    $lines = @(& $adb -s $Serial logcat -d -v threadtime -T "$since" 2>$null)
    $woke = ($lines | Where-Object { $_ -match 'woke on' } | Select-Object -First 1)
    $lines | Where-Object { $_ -match 'spoken turn:|prompt tokens|turn failed|TurnHold|Fatal signal|FATAL EXCEPTION' } | ForEach-Object { $_.Substring(19, [Math]::Min(225, $_.Length - 19)) }
    $t0 = $null
    $first = $rows | ForEach-Object { if ($_ -match '^(\S+) c0=(\d+) c4=(\d+)') { if (-not $t0) { $t0 = [double]$Matches[1] }; if ([long]$Matches[3] -gt 1037000) { [math]::Round([double]$Matches[1] - $t0, 1) } } } | Select-Object -First 1
    $top = ($rows | ForEach-Object { if ($_ -match 'c4=(\d+)') { [long]$Matches[1] } } | Measure-Object -Maximum).Maximum
    "big cores: top $top; first above the dark cap $first s after sampling began (the turn began 0.5 s in)"
    "display after the turn: $(Display)"
}
& $adb -s $Serial shell "rm /data/local/tmp/clocks.sh"
"=== finished $(Get-Date -Format HH:mm:ss)"
