# One recorded question with the phone's clocks, the memory bus and the brain's thread placement sampled every 200 ms. Reads only.
param([string]$Serial = 'UTKDU19919000815', [string]$Clip = 'q-bird', [int]$Seconds = 16, [string]$Out = 'clocks')
$adb = 'C:\Program Files (x86)\Android\android-sdk\platform-tools\adb.exe'
$pkg = 'com.bhengubv.circleai.service'
$sp = '.'
& $adb -s $Serial shell "dumpsys power | grep -E 'mWakefulness=|Display Power: state'"
$w = "$(& $adb -s $Serial shell "pidof $pkg`:inference" 2>$null)".Trim()
function MajFlt { $st = (& $adb -s $Serial shell "cat /proc/$w/stat") -join ' '; (($st -replace '^.*\) ', '') -split ' ')[9] }
$before = MajFlt
"inference pid $w; major faults before: $before"
$script = @"
w=$w
n=`$(( $Seconds * 5 ))
i=0
while [ `$i -lt `$n ]; do
  line="`$(date +%s.%N | cut -c1-14)"
  line="`$line c0=`$(cat /sys/devices/system/cpu/cpu0/cpufreq/scaling_cur_freq) c4=`$(cat /sys/devices/system/cpu/cpu4/cpufreq/scaling_cur_freq)"
  line="`$line ddr=`$(cat /sys/class/devfreq/ddrfreq/cur_freq)"
  echo "`$line"
  sleep 0.2
  i=`$((i+1))
done
"@
$tmp = [IO.Path]::GetTempFileName()
[IO.File]::WriteAllText($tmp, $script.Replace("`r`n", "`n"))
& $adb -s $Serial push $tmp /data/local/tmp/clocks.sh 2>$null | Out-Null
Remove-Item $tmp
$since = "$(& $adb -s $Serial shell "date '+%m-%d %H:%M:%S.000'" 2>$null)".Trim()
$job = Start-Job -ScriptBlock { param($a, $s) & $a -s $s shell "sh /data/local/tmp/clocks.sh" } -ArgumentList $adb, $Serial
Start-Sleep -Milliseconds 400
& $adb -s $Serial shell "am broadcast -n $pkg/ai.circle.SoakReceiver -a com.bhengubv.circleai.action.SOAK --es do talk --es file $Clip.wav" 2>$null | Out-Null
$rows = Receive-Job -Job $job -Wait -AutoRemoveJob
& $adb -s $Serial shell "rm /data/local/tmp/clocks.sh"
$rows | Set-Content "$sp\$Out.txt"
$after = MajFlt
"major faults after: $after"
Start-Sleep -Seconds 4
& $adb -s $Serial logcat -d -v time -T "$since" 2>$null | Where-Object { $_ -match 'spoken turn:|prompt tokens|where the time went|woke on|captured' } | ForEach-Object { $_.Substring(6, [Math]::Min(250, $_.Length - 6)) }
"--- samples: $(@($rows).Count)"
function Top([string]$key) { ($rows | ForEach-Object { if ($_ -match "$key=(\d+)") { [long]$Matches[1] } } | Measure-Object -Maximum).Maximum }
function Tally([string]$key) { ($rows | ForEach-Object { if ($_ -match "$key=(\d+)") { $Matches[1] } } | Group-Object | Sort-Object { [long]$_.Name } | ForEach-Object { "$($_.Name)x$($_.Count)" }) -join ' ' }
"big cores: top $(Top 'c4'); $(Tally 'c4')"
"little cores: top $(Top 'c0'); $(Tally 'c0')"
"memory bus: top $(Top 'ddr'); $(Tally 'ddr')"
