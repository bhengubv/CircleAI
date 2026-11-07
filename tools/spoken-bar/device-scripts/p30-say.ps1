# The voice alone: renders a sentence a few times and samples which cores the service's busy threads sit on while it does.
# Reads /proc only; no setting changes.
param([string]$Serial = 'UTKDU19919000815', [string]$Text = 'The capital of Kenya is Nairobi, a large and busy city in the east of Africa.', [int]$Times = 3, [int]$Samples = 12, [string]$Extra = '')
$ErrorActionPreference = 'Continue'
$adb = 'C:\Program Files (x86)\Android\android-sdk\platform-tools\adb.exe'
$pkg = 'com.bhengubv.circleai.service'
$svc = ("$(& $adb -s $Serial shell "pidof $pkg" 2>$null)".Trim() -split '\s+')[0]
"service pid $svc; cpuset: $(& $adb -s $Serial shell "cat /proc/$svc/cpuset" 2>$null) ; cgroup: $(& $adb -s $Serial shell "cat /proc/$svc/cgroup" 2>$null)"
"cpusets: top-app $(& $adb -s $Serial shell "cat /dev/cpuset/top-app/cpus" 2>$null) | foreground $(& $adb -s $Serial shell "cat /dev/cpuset/foreground/cpus" 2>$null) | background $(& $adb -s $Serial shell "cat /dev/cpuset/background/cpus" 2>$null) | system-background $(& $adb -s $Serial shell "cat /dev/cpuset/system-background/cpus" 2>$null)"
$since = "$(& $adb -s $Serial shell "date '+%m-%d %H:%M:%S.000'" 2>$null)".Trim()
$quoted = $Text.Replace(' ', '\ ').Replace(',', '\,')
& $adb -s $Serial shell "am broadcast -n $pkg/ai.circle.SoakReceiver -a com.bhengubv.circleai.action.SOAK --es do say --es text '$Text' --ei times $Times $Extra" 2>$null | Out-Null
Start-Sleep -Milliseconds 1200
$script = @'
for t in /proc/PID/task/*; do s=$(cat $t/stat 2>/dev/null); set -- ${s#*) }; if [ "$1" = "R" ]; then echo "${t##*/}:${37}"; fi; done
'@.Replace('PID', $svc)
$cores = @{}
$threads = @{}
for ($i = 0; $i -lt $Samples; $i++) {
    $o = & $adb -s $Serial shell $script 2>$null
    foreach ($line in @($o)) {
        if ($line -match '^(\d+):(\d+)') {
            $cores[$Matches[2]] = 1 + [int]$cores[$Matches[2]]
            $threads[$Matches[1]] = 1 + [int]$threads[$Matches[1]]
        }
    }
    Start-Sleep -Milliseconds 300
}
"running threads by core over $Samples samples: " + (($cores.GetEnumerator() | Sort-Object { [int]$_.Key } | ForEach-Object { "cpu$($_.Key)=$($_.Value)" }) -join ' ')
"busiest threads: " + (($threads.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 8 | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ' ')
Start-Sleep -Seconds 10
& $adb -s $Serial logcat -d -v threadtime -T "$since" 2>$null | Where-Object { $_ -match 'say:|tts: \d|CircleAI\.Turn' } | ForEach-Object { $_.Substring(19, [Math]::Min(170, $_.Length - 19)) }
