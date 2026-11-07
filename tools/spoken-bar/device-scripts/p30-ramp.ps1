# One recorded question from a dark screen, with the clocks logged on the phone against the phone's own time, and the turn's events beside them.
param([string]$Serial = 'UTKDU19919000815', [string]$Clip = 'q-bird', [int]$DarkFor = 8, [int]$Samples = 110)
$adb = 'C:\Program Files (x86)\Android\android-sdk\platform-tools\adb.exe'
$pkg = 'com.bhengubv.circleai.service'
$sampler = @'
n=$1
i=0
while [ $i -lt $n ]; do
  echo "$(date +%H:%M:%S.%N | cut -c1-12) $(cat /sys/devices/system/cpu/cpu0/cpufreq/scaling_cur_freq) $(cat /sys/devices/system/cpu/cpu4/cpufreq/scaling_cur_freq)"
  sleep 0.1
  i=$((i+1))
done
'@
$tmp = [IO.Path]::GetTempFileName()
[IO.File]::WriteAllText($tmp, $sampler.Replace("`r`n", "`n"))
& $adb -s $Serial push $tmp /data/local/tmp/clocks.sh 2>$null | Out-Null
Remove-Item $tmp
$f = "$(& $adb -s $Serial shell "dumpsys window | grep mCurrentFocus")"
if ($f -notmatch 'com\.huawei\.android\.launcher/|com\.bhengubv\.circleai\.hybrid/|StatusBar') { "SOMETHING ELSE IS IN FRONT - STOPPING: $f"; exit 0 }
& $adb -s $Serial shell "input keyevent KEYCODE_SLEEP"
Start-Sleep -Seconds $DarkFor
$since = "$(& $adb -s $Serial shell "date '+%m-%d %H:%M:%S.000'" 2>$null)".Trim()
& $adb -s $Serial shell "sh /data/local/tmp/clocks.sh $Samples > /data/local/tmp/clocks.txt 2>&1 &"
Start-Sleep -Milliseconds 1200
& $adb -s $Serial shell "am broadcast -n $pkg/ai.circle.SoakReceiver -a com.bhengubv.circleai.action.SOAK --es do talk --es file $Clip.wav" 2>$null | Out-Null
Start-Sleep -Seconds 32
$rows = @(& $adb -s $Serial shell "cat /data/local/tmp/clocks.txt")
& $adb -s $Serial shell "rm /data/local/tmp/clocks.sh /data/local/tmp/clocks.txt"
$lines = @(& $adb -s $Serial logcat -d -v time -T "$since" 2>$null)
$events = $lines | Where-Object { $_ -match 'woke on|captured|heard:|spoken turn:|prompt tokens|read ahead|tts: \d+ chars|the live ears|mScreenOn|DisplayPowerController.*(ON|OFF)' } | ForEach-Object { $_.Substring(6, [Math]::Min(170, $_.Length - 6)) }
$events
"--- clocks, when either changed (time, little, big), $($rows.Count) samples"
$last = ''
foreach ($r in $rows) { $p = "$r".Trim() -split ' '; if ($p.Count -lt 3) { continue }; $k = "$($p[1]) $($p[2])"; if ($k -ne $last) { "{0} little {1,4} big {2,4}" -f $p[0], ([int]($p[1] / 1000)), ([int]($p[2] / 1000)); $last = $k } }
