# The first spoken turn after the service starts, with the whole story from both processes kept in a file. App-scoped only.
param([string]$Serial = 'UTKDU19919000815', [string]$Clip = 'q-rain', [int]$Wait = 32)
$ErrorActionPreference = 'Continue'
$adb = 'C:\Program Files (x86)\Android\android-sdk\platform-tools\adb.exe'
$pkg = 'com.bhengubv.circleai.service'
$scratch = '.'
& "$scratch\p30-put.ps1" | Select-Object -Last 3
$since = "$(& $adb -s $Serial shell "date '+%m-%d %H:%M:%S.000'" 2>$null)".Trim()
& $adb -s $Serial shell "am broadcast -n $pkg/ai.circle.SoakReceiver -a com.bhengubv.circleai.action.SOAK --es do talk --es file $Clip.wav" 2>$null | Out-Null
Start-Sleep -Seconds $Wait
$svc = "$(& $adb -s $Serial shell "pidof $pkg" 2>$null)".Trim()
$inf = "$(& $adb -s $Serial shell "pidof ${pkg}:inference" 2>$null)".Trim()
$a = @(& $adb -s $Serial logcat -d -v threadtime -T "$since" --pid=$svc 2>$null)
$b = @(& $adb -s $Serial logcat -d -v threadtime -T "$since" --pid=$inf 2>$null)
($a + $b) | Sort-Object { $_.Substring(0, 18) } | Set-Content -Path "$scratch\p30-first.log" -Encoding utf8
($a + $b) | Sort-Object { $_.Substring(0, 18) } | Where-Object { $_ -match 'CircleAI\.(Asks|WakeTurn|Engine|Voice|Soak|Inference|Worker|Link)' -and $_ -notmatch 'kws: at|tts: alphabet|where the time' } | ForEach-Object { $_.Substring(6, [Math]::Min(180, $_.Length - 6)) }
