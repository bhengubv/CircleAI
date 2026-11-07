# S4 checkpoint on the P30: does the app choose the right brain by itself, and say why?
#  1. the engine check on the C# engine records the 2B's speed on the phone (the row's own engine now);
#  2. one real question records the running brain's speed;
#  3. the inference process is ended and a second question makes the supervisor choose again - its "plan:"
#     line is the evidence. Every matching line is kept as it arrives (the P30's log holds about two minutes).
$ErrorActionPreference = 'Continue'
$adb = "C:\Program Files (x86)\Android\android-sdk\platform-tools\adb.exe"
$s = "UTKDU19919000815"
$recv = "-n com.bhengubv.circleai.service/ai.circle.SoakReceiver -a com.bhengubv.circleai.action.SOAK"
$out = ".\p30-s4-checkpoint.log"
$kept = [System.Collections.Generic.List[string]]::new()
$seen = [System.Collections.Generic.HashSet[string]]::new()
$pattern = 'CircleAI\.(Supervisor|Engine|Asks|Worker|Soak|Self)|CIRCLEAI-(SLOT|TURN)|Fatal signal|FATAL EXCEPTION'

function Now { "$(& $adb -s $s shell "date '+%m-%d %H:%M:%S.000'" 2>$null)".Trim() }
function Collect([string]$since) {
    foreach ($m in @(& $adb -s $s logcat -d -T "$since" 2>$null | Select-String -Pattern $pattern)) {
        if ($seen.Add($m.Line)) { $kept.Add($m.Line) }
    }
}
function WaitFor([string]$since, [string]$until, [int]$minutes) {
    $t = Get-Date
    while (((Get-Date) - $t).TotalMinutes -lt $minutes) {
        Start-Sleep -Seconds 4
        Collect $since
        if (@($kept | Where-Object { $_ -match $until }).Count -gt 0) { return $true }
    }
    return $false
}

"service pid: [$("$(& $adb -s $s shell pidof com.bhengubv.circleai.service 2>$null)".Trim())]"

# 1. The C# engine's speed, on the phone, from the hash-checked sideload copy.
$t1 = Now
& $adb -s $s shell "am broadcast $recv --es do engine --es engine Managed --ei max 48 --es dir /storage/emulated/0/Android/data/com.bhengubv.circleai.service/files/models" 2>$null | Select-Object -Last 1
"engine check done: $(WaitFor $t1 'engine check done' 12)"

# 2. One real question, so the running brain's speed is recorded too.
$t2 = Now
& $adb -s $s shell "am broadcast $recv --es do ask --es text 'Tell me two short facts about the moon.'" 2>$null | Select-Object -Last 1
"first answer: $(WaitFor $t2 'CIRCLEAI-TURN' 6)"

# 3. A fresh choice: end the worker, ask again, read the plan.
Start-Sleep -Seconds 5
$t3 = Now
& $adb -s $s shell "am broadcast $recv --es do kill-worker" 2>$null | Select-Object -Last 1
Start-Sleep -Seconds 4
& $adb -s $s shell "am broadcast $recv --es do ask --es text 'Write one short sentence about the rain.'" 2>$null | Select-Object -Last 1
"second answer: $(WaitFor $t3 'CIRCLEAI-TURN' 6)"

$kept | Set-Content -Path $out -Encoding utf8
"--- the evidence"
$kept | Where-Object { $_ -match 'plan:|selector wanted|engine check \(|CIRCLEAI-TURN|brain ready|answered at|Fatal|FATAL' } |
    ForEach-Object { $_.Substring(0, [Math]::Min(420, $_.Length)) }
