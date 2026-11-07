# Read-only: what the phone is doing while the brain reads and answers one question. A loop on the device samples, every
# second, the two clusters' clocks, the inference process's page faults, the phone's page-in counters, and for every
# running thread its core. Nothing is written to the phone and no setting is changed; the question goes in by broadcast.
param([string]$Serial = '192.168.0.115:5555', [int]$Samples = 45, [string]$Text = 'Name three fruits that are yellow.', [string]$Tag = 'a')
$ErrorActionPreference = 'Continue'
$adb = 'C:\Program Files (x86)\Android\android-sdk\platform-tools\adb.exe'
$pkg = 'com.bhengubv.circleai.service'
$scratch = '.'
$out = "$scratch\where-$Tag.txt"

$device = @'
P=com.bhengubv.circleai.service
INF=$(pidof $P:inference)
echo "inference=$INF"
i=0
while [ $i -lt SAMPLES ]; do
  i=$((i+1))
  s=$(cat /proc/$INF/stat); r=${s##*) }; set -- $r
  echo "S $(date +%H:%M:%S) little=$(cat /sys/devices/system/cpu/cpu0/cpufreq/scaling_cur_freq) big=$(cat /sys/devices/system/cpu/cpu4/cpufreq/scaling_cur_freq) minflt=$8 majflt=${10} ut=${12} st=${13} $(grep -E '^MemAvailable' /proc/meminfo | tr -s ' ')"
  for t in /proc/$INF/task/*; do
    s=$(cat $t/stat 2>/dev/null); [ -z "$s" ] && continue
    r=${s##*) }; set -- $r
    [ "$1" = "R" ] && echo "T ${t##*/} [$(cat $t/comm 2>/dev/null)] cpu=${37} ut=${12} st=${13}"
  done
  sleep 1
done
'@
$device = $device.Replace('SAMPLES', "$Samples")

$t0 = "$(& $adb -s $Serial shell "date '+%m-%d %H:%M:%S.000'" 2>$null)".Trim()
& $adb -s $Serial shell "am broadcast -n $pkg/ai.circle.SoakReceiver -a com.bhengubv.circleai.action.SOAK --es do ask --es text '$($Text.Replace("'", ''))'" 2>$null | Select-Object -Last 1

$psi = [System.Diagnostics.ProcessStartInfo]::new($adb, "-s $Serial shell sh")
$psi.RedirectStandardInput = $true; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
$psi.UseShellExecute = $false
$p = [System.Diagnostics.Process]::Start($psi)
$p.StandardInput.Write(($device -replace "`r", '') + "`nexit`n"); $p.StandardInput.Close()
$read = $p.StandardOutput.ReadToEndAsync()
[void]$p.WaitForExit(($Samples + 40) * 1000)
$read.Result | Set-Content -Path $out -Encoding utf8

$lines = @(& $adb -s $Serial logcat -d -v threadtime -T "$t0" 2>$null)
$said = @($lines | Where-Object { $_ -cmatch 'CircleAI\.(Engine|Asks|Context|Soak)\b' -and $_ -match 'prompt tokens|where the time went|CIRCLEAI-TURN|Succeeded|the turn held' })
$said | ForEach-Object { $_.Substring(0, [Math]::Min(330, $_.Length)) }
$said | Add-Content -Path $out -Encoding utf8
"sampled to $out"
