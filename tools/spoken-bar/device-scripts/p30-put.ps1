# Installs the built service in place (models and data kept), waits for it to warm and for the phone to settle. App-scoped only.
param([string]$Serial = 'UTKDU19919000815', [int]$WantIdle = 70)
$ErrorActionPreference = 'Continue'
$adb = 'C:\Program Files (x86)\Android\android-sdk\platform-tools\adb.exe'
$pkg = 'com.bhengubv.circleai.service'
$scratch = '.'
$apk = "C:\Dev\Solutions\com.bhengubv\CircleAI\src\CircleAIService\bin\Release\net10.0-android\android-arm64\$pkg-Signed.apk"
$item = Get-Item $apk
"apk $($item.Length) bytes written $($item.LastWriteTime)"
$t0 = "$(& $adb -s $Serial shell "date '+%m-%d %H:%M:%S.000'" 2>$null)".Trim()
& $adb -s $Serial install -r --no-incremental $apk 2>&1 | Where-Object { $_ -match 'Success|Failure' }
$svc = ''
for ($i = 0; $i -lt 30 -and -not $svc; $i++) { Start-Sleep -Seconds 3; $svc = "$(& $adb -s $Serial shell "pidof $pkg" 2>$null)".Trim() }
"service pid [$svc]"
for ($w = 0; $w -lt 120; $w += 5) {
    Start-Sleep -Seconds 5
    if (@(& $adb -s $Serial logcat -d -v threadtime -T "$t0" 2>$null | Select-String -Pattern "greeting warmed|greeting could not").Count -gt 0) { "warmed after $w s"; break }
}
$fatal = @(& $adb -s $Serial logcat -d -T "$t0" 2>$null | Select-String -Pattern 'Fatal signal|FATAL EXCEPTION|Abort message')
"fatal lines: $($fatal.Count)"
& "$scratch\p30-quiet.ps1" -Serial $Serial -WantIdle $WantIdle -MostMinutes 3 | Select-Object -Last 2
