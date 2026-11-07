# Runs the batched-product probe on the phone and prints what it says. No screen touched, no setting changed.
param([string]$Serial = 'UTKDU19919000815', [int]$MostSeconds = 90)
$adb = 'C:\Program Files (x86)\Android\android-sdk\platform-tools\adb.exe'
$pkg = 'com.bhengubv.circleai.service'
& $adb -s $Serial shell "dumpsys window | grep mCurrentFocus"
$since = "$(& $adb -s $Serial shell "date '+%m-%d %H:%M:%S.000'" 2>$null)".Trim()
& $adb -s $Serial shell "am broadcast -n $pkg/ai.circle.SoakReceiver -a com.bhengubv.circleai.action.SOAK --es do fast --es only batch" 2>$null | Out-Null
for ($w = 0; $w -lt $MostSeconds; $w += 5) {
    Start-Sleep -Seconds 5
    if (@(& $adb -s $Serial logcat -d -v time -T "$since" 2>$null | Select-String -Pattern 'batch done|fast failed').Count -gt 0) { break }
}
& $adb -s $Serial logcat -d -v time -T "$since" 2>$null | Where-Object { $_ -match 'batch[, ]|fast failed|Exception' } | ForEach-Object { $_.Substring(6, [Math]::Min(200, $_.Length - 6)) }
