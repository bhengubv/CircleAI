# The C# engine's speed on the P30, through the service's own engine check (the soak `engine` broadcast: the 2B
# loaded in the inference process and asked once, as every question is asked there). Each run's prompt and answer
# rates are read from the service's own line; while it runs, where the engine's workers sit is sampled read-only
# (ps), as little (cores 0-3, A53) or big (4-7, A73). Nothing is written to the phone; no setting is changed.
param([string]$Serial = '192.168.0.115:5555', [int]$Runs = 3, [int]$Max = 32, [string]$Text = 'Name one fruit that is yellow.', [string]$Tag = '', [string]$Engine = 'Managed')
$ErrorActionPreference = 'Continue'
$adb = "C:\Program Files (x86)\Android\android-sdk\platform-tools\adb.exe"
$pkg = 'com.bhengubv.circleai.service'
$scratch = "."

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

# One sample: the engine's workers' last core, and the big cores' clock.
$sample = @'
INF=$(pidof com.bhengubv.circleai.service:inference)
ps -T -p $INF -o PSR,CMD 2>/dev/null | grep 'Engine' | awk '{print $1}' | tr '\n' ' '
echo "| $(cat /sys/devices/system/cpu/cpu4/cpufreq/scaling_cur_freq 2>/dev/null)"
'@

"device $Serial; build: $((Sh "dumpsys package $pkg | grep lastUpdateTime | head -1").Trim())"
$results = @()
for ($run = 1; $run -le $Runs; $run++) {
    $t0 = (Sh "date '+%m-%d %H:%M:%S.000'").Trim()
    Sh ("am broadcast -n $pkg/ai.circle.SoakReceiver -a com.bhengubv.circleai.action.SOAK --es do engine --es engine $Engine --es model Ternary-Bonsai-2-2B --ei max $Max --es text '" + $Text.Replace("'", '') + "'") | Out-Null
    $little = 0; $big = 0; $stacked = 0; $samples = 0; $clocks = @()
    $line = $null
    $deadline = (Get-Date).AddMinutes(6)
    while (-not $line -and (Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 3
        $s = Sh $sample 20
        if ($s -match '^\s*([0-9 ]+)\|\s*(\d*)') {
            $cores = @($Matches[1].Trim() -split '\s+' | Where-Object { $_ -ne '' } | ForEach-Object { [int]$_ })
            if ($cores.Count -gt 0) {
                $samples++
                $little += @($cores | Where-Object { $_ -lt 4 }).Count
                $big += @($cores | Where-Object { $_ -ge 4 }).Count
                if (@($cores | Group-Object | Where-Object Count -gt 1).Count -gt 0) { $stacked++ }
                if ($Matches[2]) { $clocks += [int]$Matches[2] }
            }
        }
        $log = @(& $adb -s $Serial logcat -d -v threadtime -T "$t0" 2>$null)
        $line = $log | Where-Object { $_ -match "CircleAI\.Engine: engine check \($Engine\): loaded in" } | Select-Object -Last 1
        if (-not $line -and ($log | Where-Object { $_ -match "engine check \($Engine\) failed|engine check: " })) { $line = ($log | Where-Object { $_ -match 'engine check' }) -join ' / ' }
    }
    $prefill = $decode = $null
    if ($line -match '(\d+) prompt tokens in (\d+) ms, (\d+) tokens in (\d+) ms') {
        $prefill = [double]$Matches[1] * 1000 / [Math]::Max(1, [double]$Matches[2])
        $decode = [double]$Matches[3] * 1000 / [Math]::Max(1, [double]$Matches[4])
    }
    $clock = if ($clocks.Count) { '{0:0.00}-{1:0.00} GHz' -f (($clocks | Measure-Object -Minimum).Minimum / 1e6), (($clocks | Measure-Object -Maximum).Maximum / 1e6) } else { '?' }
    $r = [pscustomobject]@{
        Run = $run
        Prompt = if ($prefill) { '{0:0.00} t/s' -f $prefill } else { '?' }
        Answer = if ($decode) { '{0:0.00} t/s' -f $decode } else { '?' }
        WorkersOnLittle = if ($little + $big) { '{0} of {1}' -f $little, ($little + $big) } else { '?' }
        SamplesStacked = "$stacked of $samples"
        BigClock = $clock
    }
    $results += $r
    "--- run ${run}: " +($(if ($line) { $line.Substring([Math]::Max(0, $line.IndexOf('engine check'))) } else { 'no result within 6 min' }))
    $went = @(& $adb -s $Serial logcat -d -v threadtime -T "$t0" 2>$null) | Where-Object { $_ -match 'where the time went' } | Select-Object -Last 1
    if ($went) { "    " + $went.Substring([Math]::Max(0, $went.IndexOf('where the time went'))) }
    Start-Sleep -Seconds 20   # the brain the engine check let go loads again before the next run
}
$results | Format-Table -AutoSize | Out-String
$results | ConvertTo-Json | Set-Content -Path "$scratch\p30-speed$Tag.json" -Encoding utf8
