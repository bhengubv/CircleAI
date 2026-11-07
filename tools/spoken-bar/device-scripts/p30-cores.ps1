# Read-only: where the P30 lets the service and its inference process run, and how its cores are clocked - at rest
# and while the C# brain answers a question. Every device command only reads /proc, /sys or dumpsys; nothing is
# written to the phone and no setting is changed. The question goes through the service's own soak receiver, as
# every check in this repo does.
param([string]$Serial = '192.168.0.115:5555', [switch]$Turn, [int]$Seconds = 90, [string]$Text = 'Name three fruits that are yellow.')
$ErrorActionPreference = 'Continue'
$adb = "C:\Program Files (x86)\Android\android-sdk\platform-tools\adb.exe"
$pkg = 'com.bhengubv.circleai.service'
$scratch = "."

# A device script fed on stdin, so no quoting crosses PowerShell, Windows and the device shell.
function Sh([string]$script, [int]$seconds = 40) {
    $psi = [System.Diagnostics.ProcessStartInfo]::new($adb, "-s $Serial shell sh")
    $psi.RedirectStandardInput = $true; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
    $psi.UseShellExecute = $false
    $p = [System.Diagnostics.Process]::Start($psi)
    $p.StandardInput.Write(($script -replace "`r", '') + "`nexit`n"); $p.StandardInput.Close()
    $out = $p.StandardOutput.ReadToEndAsync()
    if (-not $p.WaitForExit($seconds * 1000)) { try { $p.Kill() } catch { }; return "(adb did not answer in $seconds s)" }
    $out.Result.TrimEnd()
}

$snapshot = @'
P=com.bhengubv.circleai.service
MAIN=$(pidof $P); INF=$(pidof $P:inference)
echo "pids: main=$MAIN inference=$INF"
for X in $MAIN $INF; do
  echo "== process $X: $(cat /proc/$X/cmdline | tr '\0' ' ')"
  grep -E '^(State|Threads|Cpus_allowed_list|voluntary_ctxt_switches|nonvoluntary_ctxt_switches):' /proc/$X/status
  echo "cgroups:"; cat /proc/$X/cgroup
  echo "oom_score_adj: $(cat /proc/$X/oom_score_adj)"
done
echo "== cpusets (group: cores)"
for G in /dev/cpuset /dev/cpuset/*/; do [ -f "$G/cpus" ] && echo "$G: $(cat $G/cpus)"; done
echo "== cores (top clock, cap now, clock now, governor)"
for C in /sys/devices/system/cpu/cpu[0-9]; do
  N=$(basename $C)
  echo "$N online=$(cat $C/online 2>/dev/null || echo 1) max=$(cat $C/cpufreq/cpuinfo_max_freq 2>/dev/null) cap=$(cat $C/cpufreq/scaling_max_freq 2>/dev/null) now=$(cat $C/cpufreq/scaling_cur_freq 2>/dev/null) gov=$(cat $C/cpufreq/scaling_governor 2>/dev/null)"
done
echo "== thermal zones"
for Z in /sys/class/thermal/thermal_zone*; do echo "$(cat $Z/type 2>/dev/null)=$(cat $Z/temp 2>/dev/null)"; done | head -12
echo "== how Android holds the service"
dumpsys activity processes $P | grep -E 'ProcessRecord|curProcState|setProcState|curSchedGroup|setSchedGroup|adjType|curAdj' | head -24
'@

"=== $(Get-Date -Format HH:mm:ss) at rest"
Sh $snapshot

if ($Turn) {
    $t0 = (Sh "date '+%m-%d %H:%M:%S.000'").Trim()
    "=== $(Get-Date -Format HH:mm:ss) asking: $Text"
    Sh ("am broadcast -n $pkg/ai.circle.SoakReceiver -a com.bhengubv.circleai.action.SOAK --es do ask --es text '" + $Text.Replace("'", '') + "'") | Select-Object -Last 1
    $sample = @'
INF=$(pidof com.bhengubv.circleai.service:inference)
echo "-- $(date +%H:%M:%S) inference=$INF"
ps -T -p $INF -o TID,PSR,%CPU,CMD 2>/dev/null | awk 'NR==1 || $3+0 > 5'
for C in /sys/devices/system/cpu/cpu[0-9]; do printf "%s=%s " $(basename $C) $(cat $C/cpufreq/scaling_cur_freq 2>/dev/null); done; echo
'@
    $end = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $end) { Sh $sample; Start-Sleep -Seconds 3 }
    "=== $(Get-Date -Format HH:mm:ss) after the turn"
    Sh $snapshot
    "=== what the service said about the turn"
    $lines = @(& $adb -s $Serial logcat -d -v threadtime -T "$t0" 2>$null)
    $lines | Where-Object { $_ -cmatch 'CircleAI|CIRCLEAI' -and $_ -match 'ask: ask|brain ready|plan:|threads|kernel|CIRCLEAI-TURN|CIRCLEAI-PROMPT|tokens a second|is working on it|Balance' } |
        ForEach-Object { $_.Substring(0, [Math]::Min(360, $_.Length)) }
    $lines | Set-Content -Path "$scratch\p30-cores-turn.log" -Encoding utf8
}
