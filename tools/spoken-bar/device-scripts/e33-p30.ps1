# OPEN-GAPS E33 on the P30: the watchdog against a slow C# prefill, and a picture's failure set against the brain
# that was seeing. Broadcasts only - no screen, no device setting. The helpers are p30-s9.ps1's.
#   slow  the C# 2B pinned, one question in the adb session through the queue: its turn (persona, the session's
#         history, the question) is read at about two tokens a second and writes nothing for well over 90 s.
#         Wanted: "... is working on it (N cores) on Ternary-Bonsai-2-2B (main); waiting", no "no progress ...
#         ending the worker", the inference process alive throughout, no TimedOut HEALTH line and no Wolverine
#         remedy; the queue's 2-min deadline ends the ask, and nothing a minute past it ("past its deadline").
#   see   unpinned, then the S9 picture through the queue: on this phone it reaches SmolVLM, which fails (E32).
#         Wanted: the HEALTH line attempted "Prefill SmolVLM-256M-Instruct-MNN (Vision specialist)", not the
#         main brain.
param(
    [string[]]$Steps = @('slow', 'see'),
    [string]$Serial = '192.168.0.115:5555',
    [string]$Tag = '')
$ErrorActionPreference = 'Continue'
$adb = "C:\Program Files (x86)\Android\android-sdk\platform-tools\adb.exe"
$pkg = "com.bhengubv.circleai.service"
$recv = "-n $pkg/ai.circle.SoakReceiver -a com.bhengubv.circleai.action.SOAK"
$scratch = "."
$stream = "$scratch\e33-logcat-$(Get-Date -Format HHmmss)$Tag.log"
$wanted = 'pinned for a test|test pin is let go|brain was released|plan:|loading |brain ready|has not loaded|validat|is working on it|no progress on ask|past its deadline|ending the worker|stopped while|was (loading|reading|answering|working)|HEALTH|CircleAI\.Heal|CIRCLEAI-TURN|CIRCLEAI-SLOT|effort:|interrupted|(ask|see): ask|see: (refused|no picture)|has died|Fatal signal|FATAL EXCEPTION|Abort message'
# Ours only, and case-sensitive: "HEALTH" alone matched com.huawei.health on 2026-11-03.
$ours = 'CircleAI|CIRCLEAI|DOTNET|Fatal signal|FATAL EXCEPTION|Abort message|lowmemorykiller|am_kill'

function Adb([string]$arguments, [int]$seconds = 45) {
    $psi = [System.Diagnostics.ProcessStartInfo]::new($adb, "-s $Serial $arguments")
    $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true; $psi.UseShellExecute = $false
    $p = [System.Diagnostics.Process]::Start($psi)
    $out = $p.StandardOutput.ReadToEndAsync()
    if (-not $p.WaitForExit($seconds * 1000)) { try { $p.Kill() } catch { }; return "(adb did not answer in $seconds s)" }
    $out.Result
}
function Send([string]$extra) { ((Adb "shell `"am broadcast $recv $extra`"") -split "`r?`n" | Where-Object { $_ } | Select-Object -Last 1) }
function InferencePid { (Adb "shell pidof ${pkg}:inference" 20).Trim() }
function Pids { "main $((Adb "shell pidof $pkg" 20).Trim()), inference $(InferencePid)" }

# The log is polled, not streamed (p30-s9.ps1 says why): what it gained since the last line taken.
$script:buffer = [System.Collections.Generic.List[string]]::new()
$script:since = $null
$script:atSince = [System.Collections.Generic.HashSet[string]]::new()
function Pull {
    $text = if ($script:since) { Adb ('logcat -d -v threadtime -T "' + $script:since + '"') 30 } else { Adb 'logcat -d -v threadtime -T 1' 30 }
    $taken = [System.Collections.Generic.List[string]]::new()
    foreach ($l in ($text -split "`r?`n")) {
        if ($l -notmatch '^(\d\d-\d\d \d\d:\d\d:\d\d\.\d{3})') { continue }
        $t = $Matches[1]
        if ($script:since -and [string]::CompareOrdinal($t, $script:since) -lt 0) { continue }
        if ($t -eq $script:since) { if (-not $script:atSince.Add($l)) { continue } }
        else { $script:since = $t; $script:atSince = [System.Collections.Generic.HashSet[string]]::new(); [void]$script:atSince.Add($l) }
        $script:buffer.Add($l); $taken.Add($l)
    }
    if ($taken.Count -gt 0) { Add-Content -Path $stream -Value $taken -Encoding utf8 }
}
function Mark { Pull; $script:buffer.Count }
function Mine([int]$from) { @($script:buffer.GetRange($from, $script:buffer.Count - $from)) }

# Waits for a line matching $until after $from (or the minutes run out), then $after more seconds of log.
function Step([string]$name, [int]$from, [string]$until, [int]$minutes, [int]$after = 0) {
    $deadline = (Get-Date).AddMinutes($minutes)
    $checked = $from
    $done = $false
    while (-not $done -and (Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 5
        Pull
        $new = @($script:buffer.GetRange($checked, $script:buffer.Count - $checked))
        $checked = $script:buffer.Count
        if (@($new | Select-String -Pattern $until).Count -gt 0) { $done = $true }
        elseif (@($new | Select-String -Pattern 'Fatal signal|FATAL EXCEPTION').Count -gt 0) { break }
    }
    if ($after -gt 0) { $end = (Get-Date).AddSeconds($after); while ((Get-Date) -lt $end) { Start-Sleep -Seconds 5; Pull } }
    $mine = Mine $from
    $mine | Set-Content -Path "$scratch\e33-$name$Tag.log" -Encoding utf8
    "=== $name $(if ($done) { 'DONE' } else { "NOT DONE within $minutes min" }) ($($mine.Count) lines; pids $(Pids))"
    @($mine | Select-String -CaseSensitive -Pattern $wanted | Where-Object { $_.Line -cmatch $ours -and $_.Line -notmatch 'CircleAI\.Voice' } |
        ForEach-Object { $_.Line.Substring(0, [Math]::Min(420, $_.Line.Length)) })
}
function Count([int]$from, [string]$pattern) { @(Mine $from | Select-String -CaseSensitive -Pattern $pattern).Count }

"device $Serial ($((Adb 'shell getprop ro.serialno').Trim())); uptime: $((Adb 'shell cat /proc/uptime').Trim())"
"in front: $((Adb 'shell "dumpsys window | grep -E mCurrentFocus | head -1"').Trim())"
"service: $(Pids); $((Adb ('shell "dumpsys package ' + $pkg + ' | grep -E ''versionCode|lastUpdateTime'' | head -2"')) -replace '\s+', ' ')"
"battery: $((Adb 'shell "dumpsys battery | grep -E ''level|temperature|powered''"') -replace '\s+', ' ')"
"memory: $((Adb 'shell "grep -E ''MemTotal|MemAvailable'' /proc/meminfo"') -replace '\s+', ' ')"
[void](Mark)

if ($Steps -contains 'validation') {
    # A PIN ON A BRAIN THAT HAS NEVER LOADED HERE MAKES IT THE PENDING CANDIDATE, and the proven brain keeps
    # answering until the supervisor's validation timer (every 15 min, the queue idle, the phone plugged in)
    # loads it and asks it three known questions. After that load, a pinned ask reaches it.
    $from = Mark
    Step 'validation' $from 'validation of Ternary-Bonsai-2-2B' 30 30
}
if ($Steps -contains 'slow') {
    $from = Mark
    Send "--es do pin --es model Ternary-Bonsai-2-2B --ei minutes 20" | Out-Null
    Step 'pin' $from 'brain was released' 2
    $workerBefore = InferencePid
    $from = Mark
    Send "--es do ask --es text 'Name one fruit that is yellow.'" | Out-Null
    # The ask's own outcome line, then 90 s more: a call that did not stop at the deadline is ended a minute past it.
    Step 'slow' $from 'ask: ask [0-9a-f]+ [A-Za-z]+ by' 10 90
    $workerAfter = InferencePid
    "inference process: $workerBefore before the ask, $workerAfter after (same = never ended)"
    "said it was working and waited: $(Count $from 'is working on it')"
    "ended for no progress: $(Count $from 'no progress on ask')"
    "ended a minute past the deadline: $(Count $from 'past its deadline')"
    "HEALTH lines: $(Count $from 'CircleAI\.Heal: HEALTH')"
    "Wolverine lines: $(Count $from 'CircleAI\.Heal')"
    "the pinned brain loaded: $(Count $from 'brain ready: Ternary-Bonsai-2-2B'); held back: $(Count $from 'Ternary-Bonsai-2-2B has not loaded')"
}
if ($Steps -contains 'see') {
    $from = Mark
    Send "--es do pin --es model none" | Out-Null
    Step 'unpin' $from 'brain was released' 2
    $from = Mark
    Send "--es do see --es file s9.png --es text 'What shapes are in this picture?'" | Out-Null
    Step 'see' $from 'see: ask [0-9a-f]+ [A-Za-z]+ by|see: (refused|no picture|pictures are not)' 12 20
    "HEALTH lines naming SmolVLM: $(Count $from 'HEALTH.*attempted=Prefill SmolVLM')"
    "HEALTH lines naming the main brain: $(Count $from 'HEALTH.*attempted=Prefill Qwen3\.5-0\.8B')"
}
"log kept in $stream"
