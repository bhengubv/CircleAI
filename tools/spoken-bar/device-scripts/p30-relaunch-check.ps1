# After an install (a relaunch): does the service still know the speeds it measured before it?
# No engine check here - the plan line must pass over the Bonsai 4B on what was read back at start.
$ErrorActionPreference = 'Continue'
$adb = "C:\Program Files (x86)\Android\android-sdk\platform-tools\adb.exe"
$s = "UTKDU19919000815"
$recv = "-n com.bhengubv.circleai.service/ai.circle.SoakReceiver -a com.bhengubv.circleai.action.SOAK"
$pattern = 'CircleAI\.(Supervisor|Asks|Worker|Self)|CIRCLEAI-TURN|Fatal signal|FATAL EXCEPTION'

function Now { "$(& $adb -s $s shell "date '+%m-%d %H:%M:%S.000'" 2>$null)".Trim() }
function Lines([string]$since) { @(& $adb -s $s logcat -d -T "$since" 2>$null | Select-String -Pattern $pattern | ForEach-Object { $_.Line }) }

"--- what it read back at start"
& $adb -s $s logcat -d 2>$null | Select-String -Pattern 'what this device knows about itself' | Select-Object -Last 2 | ForEach-Object { $_.Line }

$t = Now
& $adb -s $s shell "am broadcast $recv --es do kill-worker" 2>$null | Select-Object -Last 1
Start-Sleep -Seconds 4
& $adb -s $s shell "am broadcast $recv --es do ask --es text 'Write one short sentence about the rain.'" 2>$null | Select-Object -Last 1

$kept = @()
$deadline = (Get-Date).AddMinutes(6)
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 4
    $kept = Lines $t
    if (@($kept | Where-Object { $_ -match 'CIRCLEAI-TURN|FATAL|Fatal' }).Count -gt 0) { break }
}
"--- after the relaunch"
$kept | Where-Object { $_ -match 'plan:|selector wanted|brain ready|CIRCLEAI-TURN|Fatal|FATAL' } |
    ForEach-Object { $_.Substring(0, [Math]::Min(420, $_.Length)) }
