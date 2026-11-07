# S9 on the P30: the sign-off checks a broadcast can drive - no screen, no device setting. Each step's
# lines are kept in s9-<step><Tag>.log and the lines that answer the check are printed.
#   kernels      the C# kernels on this phone's runtime, PTQ1_0 and PQ2_0 (bench)
#   managed      the 2B answers through the C# engine (engine check, engine Managed); no thinking in it
#   asked        a question through the queue, on the brain the phone chose
#   cold-csharp  the inference process ended, then the 2B through C# again: kv=primed-disk is the primed start
#   cold-queue   the inference process ended, then a question through the queue: a cold start answers
#   soak         (-Soak) the soak for $SoakMinutes minutes, then its end
# THE LOG IS POLLED, NOT STREAMED: a short `logcat -d -T <last line's time>` every few seconds, each adb call
# under a timeout. One long-lived stream died with adb's USB link on 2026-11-02 and the run waited on it;
# the P30 keeps about two minutes of log, so a poll every 5 s loses nothing while adb answers.
# THE C# CHECKS NAME THEIR FOLDER (-Dir, the sideload folder): the store can hold an empty folder of the
# model's name that a download left before its first byte (OPEN-GAPS E29).
param(
    [string[]]$Steps = @('kernels', 'managed', 'asked', 'cold-csharp', 'cold-queue'),
    [switch]$Soak, [int]$SoakMinutes = 30,
    [string]$Serial = 'UTKDU19919000815',
    [string]$Dir = '/storage/emulated/0/Android/data/com.bhengubv.circleai.service/files/models',
    [string]$Picture = 'C:\Dev\Solutions\com.bhengubv\CircleAI\fixtures\bonsai2\2B\vision-shapes.png',
    [string]$Tag = '')
$ErrorActionPreference = 'Continue'
$adb = "C:\Program Files (x86)\Android\android-sdk\platform-tools\adb.exe"
$pkg = "com.bhengubv.circleai.service"
$recv = "-n $pkg/ai.circle.SoakReceiver -a com.bhengubv.circleai.action.SOAK"
$scratch = "."
$pattern = 'CircleAI\.(Supervisor|Asks|Worker|Self|Engine|Context|Heal|Balance|Setup|Tools?|Brain|Soak|Turn|Quality)\b|CIRCLEAI-TURN|CIRCLEAI-SLOT|CIRCLEAI-PROMPT|Fatal signal|FATAL EXCEPTION|Abort message'
$stream = "$scratch\s9-logcat-$(Get-Date -Format HHmmss)$Tag.log"

# One adb call, ended if it has not answered in $seconds - a wedged adb must not hang the run.
function Adb([string]$arguments, [int]$seconds = 45) {
    $psi = [System.Diagnostics.ProcessStartInfo]::new($adb, "-s $Serial $arguments")
    $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true; $psi.UseShellExecute = $false
    $p = [System.Diagnostics.Process]::Start($psi)
    $out = $p.StandardOutput.ReadToEndAsync()
    if (-not $p.WaitForExit($seconds * 1000)) { try { $p.Kill() } catch { }; return "(adb did not answer in $seconds s)" }
    $out.Result
}
function Send([string]$extra) { ((Adb "shell `"am broadcast $recv $extra`"") -split "`r?`n" | Where-Object { $_ } | Select-Object -Last 1) }
function Pids { "main $((Adb "shell pidof $pkg" 20).Trim()), inference $((Adb "shell pidof ${pkg}:inference" 20).Trim())" }

# Both processes' resident and peak memory and what the phone has free, read from /proc; nothing is written.
function Mem {
    $script = @'
for n in com.bhengubv.circleai.service com.bhengubv.circleai.service:inference; do
  p=$(pidof $n)
  if [ -n "$p" ]; then echo "$n $(grep -E 'VmHWM|VmRSS' /proc/$p/status | tr -s ' \t' ' ' | tr '\n' ' ')"; fi
done
grep MemAvailable /proc/meminfo | tr -s ' '
'@
    $psi = [System.Diagnostics.ProcessStartInfo]::new($adb, "-s $Serial shell sh")
    $psi.RedirectStandardInput = $true; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
    $psi.UseShellExecute = $false
    $p = [System.Diagnostics.Process]::Start($psi)
    $p.StandardInput.Write(($script -replace "`r", '') + "`nexit`n"); $p.StandardInput.Close()
    $out = $p.StandardOutput.ReadToEndAsync()
    if (-not $p.WaitForExit(20000)) { try { $p.Kill() } catch { }; return '(memory not read in 20 s)' }
    ($out.Result -split "`r?`n" | Where-Object { $_ }) -join ' | '
}

# Takes what the log has gained since the last line taken (lines at that same moment are told apart).
$script:buffer = [System.Collections.Generic.List[string]]::new()
$script:since = $null
$script:atSince = [System.Collections.Generic.HashSet[string]]::new()
function Pull {
    # DOUBLE QUOTES: Windows splits an argument on spaces and does not read single quotes, so
    # -T '11-03 00:33:04.000' reached logcat as two arguments and every poll came back empty.
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

# Waits until a line matching $until appears after $from, or the minutes run out; saves the step's lines.
function Step([string]$name, [int]$from, [string]$until, [int]$minutes) {
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
    $mine = Mine $from
    $mine | Set-Content -Path "$scratch\s9-$name$Tag.log" -Encoding utf8
    "=== $name $(if ($done) { 'DONE' } else { "NOT DONE within $minutes min" }) ($($mine.Count) lines; pids $(Pids))"
    "memory: $(Mem)"
    @($mine | Select-String -Pattern $pattern | ForEach-Object { $_.Line.Substring(0, [Math]::Min(500, $_.Line.Length)) })
}
function Kv([int]$from) { @(Mine $from | Select-String -Pattern 'engine check \(Managed\): loaded' | ForEach-Object { if ($_.Line -match ', (primed-disk|primed|reset|continued|prefix-hit|none), ') { $Matches[1] } }) -join ', ' }

function SetupBoard([string]$name) {
    $from = Mark; Send "--es do setup" | Out-Null
    Step $name $from 'setup: (\d+ of \d+ settled|nothing on the way)' 2
}
function LogTail([string]$name) {
    $from = Mark; Send "--es do logs --ei lines 10" | Out-Null
    Step $name $from 'logs: circleai-service\.log \|' 2
    Start-Sleep -Seconds 3; Pull
}

"device $Serial; uptime: $((Adb 'shell cat /proc/uptime').Trim())"
$front = Adb 'shell "dumpsys window | grep -E mCurrentFocus | head -1"'
$package = Adb ('shell "dumpsys package ' + $pkg + ' | grep -E ''versionCode|lastUpdateTime'' | head -2"')
$power = Adb 'shell "dumpsys battery | grep -E ''level|temperature|powered''"'
"in front: $($front.Trim())"
"service: $(Pids); $($package -replace '\s+', ' ')"
"battery: $($power -replace '\s+', ' ')"
[void](Mark)

if ($Steps -contains 'setup') { SetupBoard 'setup' }
if ($Steps -contains 'kernels') {
    $from = Mark; Send "--es do bench"
    Step 'kernels' $from 'bench done in' 15
}
if ($Steps -contains 'limits') {
    # WHAT LIMITS A TOKEN HERE: the real clock, memory, a block of the kernel at each level, cores, the pool, heat.
    $from = Mark; Send "--es do limits"
    Step 'limits' $from 'limits done in|limits failed' 8
}
if ($Steps -contains 'fast') {
    # HOW FAST A TERNARY PRODUCT CAN GO HERE: the probe's NEON kernels, checked and timed.
    $from = Mark; Send "--es do fast"
    Step 'fast' $from 'fast done in|fast failed|no ARM vector' 6
}
if ($Steps -contains 'managed') {
    $from = Mark; Send "--es do engine --es engine Managed --es model Ternary-Bonsai-2-2B --es dir $Dir --ei max 48 --es text 'What is the capital of Kenya? Answer in one sentence.'"
    Step 'managed' $from 'engine check done' 10
    $answer = @(Mine $from | Select-String -Pattern 'engine check \(Managed\): loaded' | ForEach-Object { $_.Line })
    "thinking leaked into the C# answer: $(if (@($answer | Select-String -Pattern '<think>|</think>').Count -gt 0) { 'YES' } elseif ($answer.Count -eq 0) { 'no answer line' } else { 'no' }); began from: $(Kv $from)"
}
if ($Steps -contains 'prompts') {
    # THE PLAN'S TWO HARD PROMPTS ON THE PHONE, the words of the fixtures' "code" and "isizulu" cases, through the C# 2B.
    foreach ($case in @(
            @{ Name = 'coding'; Text = 'Write a Python function that checks whether a number is prime.' },
            @{ Name = 'isizulu'; Text = 'Sawubona! Ungangitshela ukuthi iyini inhloko-dolobha yaseNingizimu Afrika?' })) {
        $from = Mark; Send "--es do engine --es engine Managed --es model Ternary-Bonsai-2-2B --es dir $Dir --ei max 64 --es text '$($case.Text)'" | Out-Null
        Step "prompt-$($case.Name)" $from 'engine check done' 12
    }
}
if ($Steps -contains 'prime') {
    # THE TOOLS, READ IN THE GAPS ONCE THE BRAIN IS WARM. A question brings the brain up; then nothing is
    # asked until the block is kept, so the checks below meet a brain that has it.
    $from = Mark; Send "--es do ask --es text 'Say hello in one word.'"
    Step 'prime' $from 'primed start kept: a \d{3,}-token|the tools are left unread' 25
}
if ($Steps -contains 'sum') {
    # A SUM ASKED PLAINLY, NOTHING SAID OF TOOLS: a cue is offered every tool when that is the block read.
    $from = Mark; Send "--es do ask --es text 'What is 17 times 4?'"
    Step 'sum' $from 'CIRCLEAI-TURN' 8
}
if ($Steps -contains 'grow-back') {
    # THE BRAIN GROWS BACK ONCE THE PHONE HAS BEEN CALM FOR TEN MINUTES: a shape it has held before is on disk.
    $from = Mark
    Step 'grow-back' $from 'balance: \w+ -> Full' 16
    "the tools read again: $(@(Mine $from | Select-String -Pattern 'reading a \d{3,}-token system turn').Count) times"
}
if ($Steps -contains 'cue') {
    # A TOOL CUE FROM A FRESH WORKER, TOOLS NOT ASKED FOR: a plain sum never reaches the brain (the engine's
    # arithmetic answers it), so this one does. The cue picks the calculator alone, the brain holds the block
    # of every tool, and every tool should be what it is offered.
    $from = Mark; Send "--es do kill-worker"; Start-Sleep -Seconds 6
    Send "--es do ask --es text 'How many legs does a spider have?'"
    Step 'cue' $from 'CIRCLEAI-TURN' 8
}
if ($Steps -contains 'tool-cold') {
    # AND THE TOOL TURN FROM A COLD WORKER: the block is on disk, so it starts after it.
    $from = Mark; Send "--es do kill-worker"; Start-Sleep -Seconds 6
    Send "--es do ask --es text 'Use the calculator: what is 6789 times 3?' --ez tools true"
    Step 'tool-cold' $from 'ask: ask [0-9a-f]+ [A-Za-z]+ by' 10
    "tools that ran: $(@(Mine $from | Select-String -Pattern 'tool (ran|threw): [a-z_]+ \([A-Za-z]+\)' | ForEach-Object { $_.Matches[0].Value }) -join '; ')"
}
if ($Steps -contains 'asked') {
    $from = Mark; Send "--es do ask --es text 'Write one short sentence about the rain.'"
    Step 'asked' $from 'CIRCLEAI-TURN' 8
}
if ($Steps -contains 'tool') {
    # A TOOL TURN AS AN APP SENDS ONE (OPEN-GAPS A16): a battery question, which the engine's own tool answers.
    $from = Mark; Send "--es do ask --es text 'What is my battery level?' --ez tools true"
    Step 'tool' $from 'ask: ask [0-9a-f]+ [A-Za-z]+ by' 8
    "tools that ran: $(@(Mine $from | Select-String -Pattern 'tool (ran|threw): [a-z_]+ \([A-Za-z]+\)' | ForEach-Object { $_.Matches[0].Value }) -join '; ')"
}
if ($Steps -contains 'tool-model') {
    # AND ONE THE MODEL MUST CALL FOR ITSELF: the agentic loop, its tools offered.
    $from = Mark; Send "--es do ask --es text 'Use the calculator: what is 6789 times 3?' --ez tools true"
    Step 'tool-model' $from 'ask: ask [0-9a-f]+ [A-Za-z]+ by' 10
    "tools that ran: $(@(Mine $from | Select-String -Pattern 'tool (ran|threw): [a-z_]+ \([A-Za-z]+\)' | ForEach-Object { $_.Matches[0].Value }) -join '; ')"
}
if ($Steps -contains 'logs') { LogTail 'logs' }
if ($Steps -contains 'pretend') {
    # A FLAT, HOT PHONE, PRETENDED - no device setting (PretendConditions): the balance steps the brain down.
    $from = Mark; Send "--es do pretend --ei battery 8 --ez charging false --ei heat 2 --ei minutes 10"
    Step 'pretend' $from 'balance: \w+ -> ' 6
    # And the plan read with it: the next question loads the brain, and its plan line names the battery.
    $from = Mark; Send "--es do ask --es text 'Say hello in one word.'"
    Step 'pretend-plan' $from 'CIRCLEAI-TURN' 8
    Send "--es do pretend-off" | Out-Null
}
if ($Steps -contains 'fetch-2b') {
    # THE 2B AND ITS TOWER INTO THE STORE, as setup fetches (each checked against its SHA-256, on the board).
    foreach ($m in 'Ternary-Bonsai-2-2B', 'Ternary-Bonsai-2-2B-Vision') {
        $from = Mark; Send "--es do fetch --es model $m"
        Step "fetch-$m" $from "fetch: $([regex]::Escape($m)) (landed|did not land|failed|is already here)" 30
    }
}
if ($Steps -contains 'photo-csharp') {
    # A PHOTO DESCRIBED BY THE C# 2B AND ITS TOWER (S8's sight), on the picture S8's fixture describes.
    $pushArgs = 'push "' + $Picture + '" /storage/emulated/0/Android/data/' + $pkg + '/files/s9.png'
    $pushed = Adb $pushArgs 180
    "picture: $($pushed.Trim())"
    $from = Mark; Send "--es do engine --es engine Managed --es model Ternary-Bonsai-2-2B --ei max 64 --es image s9.png --es text 'Describe this picture in one sentence.'"
    Step 'photo-csharp' $from 'engine check done' 20
}
if ($Steps -contains 'see') {
    # AND THE SAME PICTURE AS AN APP SENDS ONE: the queue, whichever brain on the phone can see.
    $from = Mark; Send "--es do see --es file s9.png --es text 'What shapes are in this picture?'"
    Step 'see' $from 'see: ask [0-9a-f]+ [A-Za-z]+ by|see: (refused|no picture|pictures are not)' 12
}
if ($Steps -contains 'too-big') {
    # A BRAIN TOO BIG FOR THIS PHONE, FORCED: Qwen3.5-2B on MNN (1.9 GB, eagerly loaded), the one whose
    # load set off the low-memory kills on 2026-10-10. Pinned whatever the plan says; Wolverine's rules
    # and the memory verdicts should step down from it, and the question still be answered.
    $from = Mark; Send "--es do fetch --es model Qwen3.5-2B-MNN"
    Step 'fetch-Qwen3.5-2B-MNN' $from 'fetch: Qwen3\.5-2B-MNN (landed|did not land|failed|is already here)' 40
    $from = Mark; Send "--es do pin --es model Qwen3.5-2B-MNN --ei minutes 15"; Start-Sleep -Seconds 8
    Send "--es do ask --es text 'Name one fruit.'"
    Step 'too-big' $from 'CIRCLEAI-TURN' 15
    "what happened to it: "
    @(Mine $from | Select-String -Pattern 'stepped down|killed for memory|OutOfMemory|lowmemorykiller|am_kill|has died|pinned for a test|brain ready|did not start|CIRCLEAI-TURN' | ForEach-Object { $_.Line.Substring(0, [Math]::Min(300, $_.Line.Length)) }) | Select-Object -Last 30
    Send "--es do pin --es model none" | Out-Null
}
if ($Steps -contains 'too-big-held') {
    # AS too-big BELOW, WITHOUT ITS FETCH: a download needs the owner's yes. The pin says what it finds.
    $from = Mark; Send "--es do pin --es model Qwen3.5-2B-MNN --ei minutes 15"; Start-Sleep -Seconds 8
    Send "--es do ask --es text 'Name one fruit.'"
    Step 'too-big' $from 'CIRCLEAI-TURN' 15
    "what happened to it: "
    @(Mine $from | Select-String -Pattern 'stepped down|killed for memory|OutOfMemory|lowmemorykiller|am_kill|has died|pinned for a test|brain ready|did not start|CIRCLEAI-TURN' | ForEach-Object { $_.Line.Substring(0, [Math]::Min(300, $_.Line.Length)) }) | Select-Object -Last 30
    Send "--es do pin --es model none" | Out-Null
}
if ($Steps -contains 'cold-csharp') {
    $from = Mark; Send "--es do kill-worker"; Start-Sleep -Seconds 6
    Send "--es do engine --es engine Managed --es model Ternary-Bonsai-2-2B --es dir $Dir --ei max 24 --es text 'Name one colour of the rainbow.'"
    Step 'cold-csharp' $from 'engine check done' 10
    "C# cold start began from: $(Kv $from)"
}
if ($Steps -contains 'cold-queue') {
    $from = Mark; Send "--es do kill-worker"; Start-Sleep -Seconds 6
    Send "--es do ask --es text 'Name one colour of the rainbow.'"
    Step 'cold-queue' $from 'CIRCLEAI-TURN' 10
}
if ($Soak) {
    $from = Mark; Send "--es do start --es run s9-p30$Tag"
    "soak started $(Get-Date -Format HH:mm:ss) for $SoakMinutes min; pids $(Pids)"
    $end = (Get-Date).AddMinutes($SoakMinutes)
    while ((Get-Date) -lt $end) { Start-Sleep -Seconds 5; Pull }
    Send "--es do stop"; Start-Sleep -Seconds 20; Pull
    $mine = Mine $from
    $mine | Set-Content -Path "$scratch\s9-soak$Tag.log" -Encoding utf8
    "=== soak: $(@($mine | Select-String -Pattern 'CIRCLEAI-TURN').Count) turns, $(@($mine | Select-String -Pattern 'Fatal signal|FATAL EXCEPTION').Count) fatal lines; pids now $(Pids)"
    @($mine | Select-String -Pattern 'CircleAI\.(Soak|Heal|Balance|Supervisor)\b|Fatal signal|FATAL EXCEPTION' | ForEach-Object { $_.Line }) | Select-Object -Last 40
}
if ($Steps -contains 'setup-end') { SetupBoard 'setup-end' }
if ($Steps -contains 'logs-end') { LogTail 'logs-end' }
"log kept in $stream"
