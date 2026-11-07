# Which notes ride a turn on the P30: the two engine-check questions, each through the inference process's own
# AIService, and the service's own lines about them - "notes for this turn" (by source and length, no words) and
# CIRCLEAI-PROMPT. Broadcasts only; nothing written to the phone, no setting changed.
param([string]$Serial = '192.168.0.115:5555')
$adb = "C:\Program Files (x86)\Android\android-sdk\platform-tools\adb.exe"
$pkg = 'com.bhengubv.circleai.service'
foreach ($text in @('Name one fruit that is yellow.', 'Count from one to twenty, in words.')) {
    $t0 = (& $adb -s $Serial shell "date '+%m-%d %H:%M:%S.000'").Trim()
    & $adb -s $Serial shell "am broadcast -n $pkg/ai.circle.SoakReceiver -a com.bhengubv.circleai.action.SOAK --es do engine --es engine Managed --es model Ternary-Bonsai-2-2B --ei max 48 --es text '$text'" | Out-Null
    $done = $false; $until = (Get-Date).AddMinutes(5); $log = @()
    while (-not $done -and (Get-Date) -lt $until) { Start-Sleep -Seconds 10; $log = @(& $adb -s $Serial logcat -d -v threadtime -T "$t0"); $done = @($log | Where-Object { $_ -match 'engine check done' }).Count -gt 0 }
    "=== $text"
    $log | Where-Object { $_ -match 'notes for this turn|CIRCLEAI-PROMPT|CIRCLEAI-SKILLS|engine check \(Managed\): loaded|where the time went' } | ForEach-Object { $_.Substring(0, [Math]::Min(300, $_.Length)) }
    Start-Sleep -Seconds 20
}
