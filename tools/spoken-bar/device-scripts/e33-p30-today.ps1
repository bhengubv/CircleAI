# OPEN-GAPS E33's fixes of 2026-11-04 on the P30. Broadcasts only - no screen, no device setting; the pretended
# heat is the service's own (PretendConditions), never the phone's. The helpers are e33-p30.ps1's.
#   sum      the C# 2B pinned (it counts its tools exactly, templateTools=N), then "How many legs do three spiders
#            have?" through the queue: a Maths cue. Wanted: templateTools=1 - the calculator alone, not the search
#            beside it - and the ask's outcome naming the 2B. The 2B reads slowly; the queue's deadline may end it.
#   see      unpinned, the S9 picture through the queue: on this phone it reaches SmolVLM, which fails (E32).
#            Wanted: the outcome line "see: ask ... by SmolVLM-256M-Instruct-MNN", not the main brain.
#   standin  a brain that is not on the phone pinned (the Bonsai 4B), then one question. Wanted: "the selector wanted
#            Ternary-Bonsai-2-4B, which is not on this phone; using Qwen3.5-0.8B-MNN" - the quick one, not the 2B.
#   balance  a critical heat pretended for an hour, until the balance's smaller-brain rung reloads the brain. Wanted:
#            "balance: Ternary-Bonsai-2-2B answered at 1.7 tokens a second here ... so Qwen3.5-0.8B-MNN stays, lean".
#            Then the pretence is let go; the brain grows back on its own after ten calm minutes.
param(
    [string[]]$Steps = @('sum', 'see', 'standin'),
    [string]$Serial = '192.168.0.115:5555',
    [int]$BalanceMinutes = 50,
    [string]$Tag = '')
$ErrorActionPreference = 'Continue'
$adb = "C:\Program Files (x86)\Android\android-sdk\platform-tools\adb.exe"
$pkg = "com.bhengubv.circleai.service"
$recv = "-n $pkg/ai.circle.SoakReceiver -a com.bhengubv.circleai.action.SOAK"
$scratch = "."
$stream = "$scratch\e33-today-logcat-$(Get-Date -Format HHmmss)$Tag.log"
$wanted = 'pinned for a test|test pin is let go|brain was released|plan:|the selector wanted|loading |brain ready|has not loaded|keeping|stepped down|killed for memory|validat|is working on it|no progress on ask|past its deadline|ending the worker|HEALTH|CircleAI\.Heal|CIRCLEAI-PROMPT|balance|Balance|pretend|(ask|see): ask|see: (refused|no picture)|has died|Fatal signal|FATAL EXCEPTION|Abort message'
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
        if (@($new | Select-String -CaseSensitive -Pattern $until).Count -gt 0) { $done = $true }
        elseif (@($new | Select-String -Pattern 'Fatal signal|FATAL EXCEPTION').Count -gt 0) { break }
    }
    if ($after -gt 0) { $end = (Get-Date).AddSeconds($after); while ((Get-Date) -lt $end) { Start-Sleep -Seconds 5; Pull } }
    $mine = Mine $from
    $mine | Set-Content -Path "$scratch\e33-today-$name$Tag.log" -Encoding utf8
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

if ($Steps -contains 'sum') {
    $from = Mark
    Send "--es do pin --es model Ternary-Bonsai-2-2B --ei minutes 10" | Out-Null
    Step 'sum-pin' $from 'brain was released' 2
    $from = Mark
    Send "--es do ask --es text 'How many legs do three spiders have?'" | Out-Null
    Step 'sum' $from 'ask: ask [0-9a-f]+ [A-Za-z]+ by' 6 10
    "tools the sum was handed: $(@(Mine $from | Select-String -CaseSensitive -Pattern 'templateTools=(\d+) query=3[0-9]' | ForEach-Object { $_.Matches[0].Groups[1].Value }) -join ', ')"
    $from = Mark
    Send "--es do pin --es model none" | Out-Null
    Step 'sum-unpin' $from 'brain was released' 2
}
if ($Steps -contains 'see') {
    $from = Mark
    Send "--es do see --es file s9.png --es text 'What shapes are in this picture?'" | Out-Null
    Step 'see' $from 'see: ask [0-9a-f]+ [A-Za-z]+ by|see: (refused|no picture|pictures are not)' 12 20
    "outcome naming SmolVLM: $(Count $from 'see: ask [0-9a-f]+ [A-Za-z]+ by SmolVLM'); naming the main brain: $(Count $from 'see: ask [0-9a-f]+ [A-Za-z]+ by Qwen3\.5-0\.8B')"
    "HEALTH lines naming SmolVLM: $(Count $from 'HEALTH.*attempted=Prefill SmolVLM'); naming the main brain: $(Count $from 'HEALTH.*attempted=Prefill Qwen3\.5-0\.8B')"
}
if ($Steps -contains 'standin') {
    $from = Mark
    Send "--es do pin --es model Ternary-Bonsai-2-4B --ei minutes 5" | Out-Null
    Step 'standin-pin' $from 'brain was released' 2
    $from = Mark
    Send "--es do ask --es text 'Name one fruit that is yellow.'" | Out-Null
    Step 'standin' $from 'ask: ask [0-9a-f]+ [A-Za-z]+ by' 6 5
    "stood in with the 0.8B: $(Count $from 'Ternary-Bonsai-2-4B, which is not on this phone; using Qwen3\.5-0\.8B-MNN'); with the 2B: $(Count $from 'Ternary-Bonsai-2-4B, which is not on this phone; using Ternary-Bonsai-2-2B')"
    $from = Mark
    Send "--es do pin --es model none" | Out-Null
    Step 'standin-unpin' $from 'brain was released' 2
}
if ($Steps -contains 'balance') {
    $from = Mark
    Send "--es do pretend --ei heat 2 --ei minutes 60" | Out-Null
    Step 'balance' $from 'Supervisor: balance: (no brain smaller|.*stays, lean|.* answers instead of)' $BalanceMinutes 20
    "stayed lean: $(Count $from 'Supervisor: balance: .*stays, lean'); stepped down: $(Count $from 'Supervisor: balance: .* answers instead of')"
    $from = Mark
    Send "--es do pretend-off" | Out-Null
    Step 'balance-off' $from 'pretend off' 1
}
"log kept in $stream"
