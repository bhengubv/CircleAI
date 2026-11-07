# Turns the screen off (the owner's yes, 2026-11-06), reads the core groups, then runs recorded questions through the whole turn.
# The first two turns are also sampled part way through; the rest are left alone so the sampling cannot touch their timing.
param([string]$Serial = 'UTKDU19919000815', [string[]]$Clips = @('q-bird','q-sky','q-rain','q-bird','q-sky','q-rain','q-bird','q-sky'), [int]$Each = 24)
$adb = 'C:\Program Files (x86)\Android\android-sdk\platform-tools\adb.exe'
$pkg = 'com.bhengubv.circleai.service'
$sp = '.'
function Rank { & "$sp\p30-state.ps1" -Serial $Serial | Select-String -Pattern 'mWakefulness|Display Power|cpuset=|Proc #.*circleai\.service|/foreground$|/key-background$|/background$|/restricted$|pid \d+:' | ForEach-Object { $_.Line } }
function Groups { & $adb -s $Serial shell 'for g in foreground key-background background restricted top-app; do echo "$g=$(cat /dev/cpuset/$g/cpus)"; done' }

$f = "$(& $adb -s $Serial shell "dumpsys window | grep mCurrentFocus")"
"focus: $f"
if ($f -notmatch 'com\.huawei\.android\.launcher/|com\.bhengubv\.circleai\.hybrid/') { 'SOMETHING ELSE IS IN FRONT - STOPPING'; exit 0 }

"=== screen on, $(Get-Date -Format HH:mm:ss)"
Groups
"=== turning the screen off, $(Get-Date -Format HH:mm:ss)"
& $adb -s $Serial shell "input keyevent KEYCODE_SLEEP"
Start-Sleep -Seconds 8
Groups
Rank
& "$sp\p30-quiet.ps1" -Serial $Serial -WantIdle 75 -MostMinutes 2 | Select-Object -Last 1

$n = 0
foreach ($clip in $Clips) {
    $n++
    $since = "$(& $adb -s $Serial shell "date '+%m-%d %H:%M:%S.000'" 2>$null)".Trim()
    "---- $clip at $since"
    & $adb -s $Serial shell "am broadcast -n $pkg/ai.circle.SoakReceiver -a com.bhengubv.circleai.action.SOAK --es do talk --es file $clip.wav" 2>$null | Out-Null
    if ($n -le 2) {
        Start-Sleep -Seconds 4
        "mid-turn:"
        Rank
        Start-Sleep -Seconds ($Each - 6)
    } else {
        Start-Sleep -Seconds $Each
    }
    $lines = @(& $adb -s $Serial logcat -d -v threadtime -T "$since" 2>$null)
    $lines | Where-Object { $_ -match 'spoken turn:|prompt tokens|turn failed|TurnHold|Fatal signal|FATAL EXCEPTION' -and $_ -notmatch 'kws: at' } |
        ForEach-Object { $_.Substring(19, [Math]::Min(230, $_.Length - 19)) }
}
"=== finished $(Get-Date -Format HH:mm:ss)"
& $adb -s $Serial shell "dumpsys power | grep -E 'mWakefulness=|Display Power: state'"
& $adb -s $Serial shell 'dumpsys midi | grep -c "DeviceConnection count: 0"'
Groups
