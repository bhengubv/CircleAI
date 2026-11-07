# The ears alone: stretches of a recording, timed, with what they heard. Reads a test recording only; no setting changes.
param([string]$Serial = 'UTKDU19919000815', [string]$File = 'q-rain.wav', [string[]]$Stretches = @('0-0', '1000-2500', '1000-2000', '2000-3900', '3000-4400'), [int]$Times = 2)
$ErrorActionPreference = 'Continue'
$adb = 'C:\Program Files (x86)\Android\android-sdk\platform-tools\adb.exe'
$pkg = 'com.bhengubv.circleai.service'
$since = "$(& $adb -s $Serial shell "date '+%m-%d %H:%M:%S.000'" 2>$null)".Trim()
foreach ($s in $Stretches) {
    $a, $b = $s -split '-'
    & $adb -s $Serial shell "am broadcast -n $pkg/ai.circle.SoakReceiver -a com.bhengubv.circleai.action.SOAK --es do hear --es file $File --ei from $a --ei to $b --ei times $Times" 2>$null | Out-Null
    Start-Sleep -Seconds (3 * $Times + 1)
}
& $adb -s $Serial logcat -d -v threadtime -T "$since" 2>$null | Where-Object { $_ -match 'hear:|stt: \d' } | ForEach-Object { $_.Substring(19, [Math]::Min(180, $_.Length - 19)) }
