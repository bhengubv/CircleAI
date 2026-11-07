# Recorded questions under different ways of coming out of the dark, with the big cores' clock during the part of the turn that counts.
#   lock:  dark, then only the turn's own wake lock lights the screen
#   key1:  dark, a wake key 1 s before the turn       key20: dark, a wake key 20 s before the turn
#   stay:  no dark at all, the screen as the last turn left it
param([string]$Serial = 'UTKDU19919000815', [string[]]$Modes = @('lock','key1','key20','stay','lock','key1','key20','stay'), [string[]]$Clips = @('q-bird'), [int]$DarkFor = 8, [switch]$Detail)
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
function Faults {
    $out = @()
    foreach ($name in $pkg, "$pkg`:inference") {
        $id = "$(& $adb -s $Serial shell "pidof $name" 2>$null)".Trim()
        $st = (& $adb -s $Serial shell "cat /proc/$id/stat") -join ' '
        $out += [long]((($st -replace '^.*\) ', '') -split ' ')[9])
    }
    return $out
}
$n = 0
foreach ($mode in $Modes) {
    $clip = $Clips[$n % $Clips.Count]; $n++
    $f = "$(& $adb -s $Serial shell "dumpsys window | grep mCurrentFocus")"
    if ($f -notmatch 'com\.huawei\.android\.launcher/|com\.bhengubv\.circleai\.hybrid/|StatusBar') { "SOMETHING ELSE IS IN FRONT - STOPPING: $f"; break }
    if ($mode -ne 'stay') {
        & $adb -s $Serial shell "input keyevent KEYCODE_SLEEP"
        Start-Sleep -Seconds $DarkFor
    }
    if ($mode -eq 'key1') { & $adb -s $Serial shell "input keyevent KEYCODE_WAKEUP"; Start-Sleep -Seconds 1 }
    if ($mode -eq 'key20') { & $adb -s $Serial shell "input keyevent KEYCODE_WAKEUP"; Start-Sleep -Seconds 20 }
    $since = "$(& $adb -s $Serial shell "date '+%m-%d %H:%M:%S.000'" 2>$null)".Trim()
    $faults0 = Faults
    & $adb -s $Serial shell "sh /data/local/tmp/clocks.sh 120 > /data/local/tmp/clocks.txt 2>&1 &"
    Start-Sleep -Milliseconds 800
    & $adb -s $Serial shell "am broadcast -n $pkg/ai.circle.SoakReceiver -a com.bhengubv.circleai.action.SOAK --es do talk --es file $clip.wav" 2>$null | Out-Null
    Start-Sleep -Seconds 28
    $rows = @(& $adb -s $Serial shell "cat /data/local/tmp/clocks.txt")
    $lines = @(& $adb -s $Serial logcat -d -v time -T "$since" 2>$null)
    $captured = ($lines | Where-Object { $_ -match 'captured \d+ bytes' } | Select-Object -First 1)
    $turn = ($lines | Where-Object { $_ -match 'spoken turn:' } | Select-Object -First 1)
    $from = if ($captured) { $captured.Substring(6, 12) } else { '' }
    $big = @(); $little = @()
    if ($from) {
        $until = ([datetime]::ParseExact($from, 'HH:mm:ss.fff', $null)).AddSeconds(3).ToString('HH:mm:ss.fff')
        foreach ($r in $rows) { $p = "$r".Trim() -split ' '; if ($p.Count -ge 3 -and $p[0] -ge $from -and $p[0] -le $until) { $big += [int]($p[2] / 1000); $little += [int]($p[1] / 1000) } }
    }
    $tally = ($big | Group-Object | Sort-Object { [int]$_.Name } | ForEach-Object { "$($_.Name)x$($_.Count)" }) -join ' '
    $short = if ($turn -match 'spoken turn: (\d+) ms.*ears (-?\d+), brain (\d+), to the voice (\d+), voice (\d+)') { "turn $($Matches[1]) ms (ears $($Matches[2]), brain $($Matches[3]), to voice $($Matches[4]), voice $($Matches[5]))" } else { 'no turn line' }
    $silence = if ($turn -match 'longest silence inside the answer (\d+) ms') { $Matches[1] } else { '?' }
    $heat = ((& $adb -s $Serial shell "dumpsys thermalservice | grep -A6 'Current temperatures' | grep -E 'cluster1|Battery'") | ForEach-Object { if ($_ -match 'mValue=([\d.]+).*mName=(\w+)') { "$($Matches[2]) $($Matches[1])" } }) -join ', '
    "{0,-6} {1,-7} {2}; gap {3}; big after the mic closed: {4}; {5}" -f $mode, $clip, $short, $silence, $tally, $heat
    if ($Detail) {
        $faults1 = Faults
        "       major faults during the turn: service $($faults1[0] - $faults0[0]), brain $($faults1[1] - $faults0[1])"
        $lines | Where-Object { $_ -match 'prompt tokens|where the time went|read ahead to|the live ears|tts: \d+ chars|stopped after' } | Select-Object -First 9 | ForEach-Object { "       " + $_.Substring(6, [Math]::Min(215, $_.Length - 6)) }
    }
}
& $adb -s $Serial shell "rm -f /data/local/tmp/clocks.sh /data/local/tmp/clocks.txt"
"=== finished $(Get-Date -Format HH:mm:ss)"
