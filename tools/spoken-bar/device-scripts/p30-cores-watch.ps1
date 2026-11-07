# Starts the core-group watch off screen, brings our own app up and sends it away again, and prints what moved. App-scoped only.
param([string]$Serial = 'UTKDU19919000815')
$adb = 'C:\Program Files (x86)\Android\android-sdk\platform-tools\adb.exe'
$pkg = 'com.bhengubv.circleai.service'
$app = 'com.bhengubv.circleai.hybrid/crc647b03502b5e811282.MainActivity'
function Focus { "$(& $adb -s $Serial shell "dumpsys window | grep mCurrentFocus")" }
$f = Focus
"focus: $f"
if ($f -notmatch 'com\.huawei\.android\.launcher/|com\.bhengubv\.circleai\.hybrid/') { 'SOMETHING ELSE IS IN FRONT - STOPPING'; exit 0 }
$since = "$(& $adb -s $Serial shell "date '+%m-%d %H:%M:%S.000'" 2>$null)".Trim()
& $adb -s $Serial shell "am broadcast -n $pkg/ai.circle.SoakReceiver -a com.bhengubv.circleai.action.SOAK --es do cores --ez watch true" 2>$null | Out-Null
Start-Sleep -Seconds 15
"-- bringing our app up at $(Get-Date -Format HH:mm:ss)"
& $adb -s $Serial shell "am start -n $app" 2>$null | Out-Null
Start-Sleep -Seconds 15
$f = Focus
"focus: $f"
if ($f -match 'com\.bhengubv\.circleai\.hybrid/') {
    "-- home at $(Get-Date -Format HH:mm:ss)"
    & $adb -s $Serial shell "input keyevent KEYCODE_HOME"
}
Start-Sleep -Seconds 15
"focus: $(Focus)"
& $adb -s $Serial logcat -d -v time -T "$since" 2>$null | Where-Object { $_ -match 'cores:' } | ForEach-Object { $_.Substring(6, [Math]::Min(150, $_.Length - 6)) }
