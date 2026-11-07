# The streaming ears alone on the P30, at the pace a person speaks: recorded test questions only, no setting changes.
param([string]$Serial = 'UTKDU19919000815', [string]$Model = 'tiny-streaming-en', [string[]]$Clips = @('q-bird.wav', 'q-sky.wav'),
      [int]$Every = 500, [int]$Threads = 0, [string]$Spin = 'true', [string]$Prepack = 'true', [int]$Times = 2, [switch]$All, [string]$Pin = 'false', [int]$Left = 0)
$ErrorActionPreference = 'Continue'
$adb = 'C:\Program Files (x86)\Android\android-sdk\platform-tools\adb.exe'
$pkg = 'com.bhengubv.circleai.service'
$since = "$(& $adb -s $Serial shell "date '+%m-%d %H:%M:%S.000'" 2>$null)".Trim()
$want = 0
foreach ($clip in $Clips) {
    $want += $Times
    & $adb -s $Serial shell "am broadcast -n $pkg/ai.circle.SoakReceiver -a com.bhengubv.circleai.action.SOAK --es do ears --es file $clip --es model $Model --ei every $Every --ei threads $Threads --ez spin $Spin --ez prepack $Prepack --ei times $Times --ez pin $Pin --ei left $Left" 2>$null | Out-Null
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Seconds 3
        $got = @(& $adb -s $Serial logcat -d -v threadtime -T "$since" 2>$null | Where-Object { $_ -match "ears: spent|ears: \w+Exception" })
        if ($got.Count -ge $want -or @($got | Where-Object { $_ -match 'Exception' }).Count -gt 0) { break }
    }
}
$lines = @(& $adb -s $Serial logcat -d -v threadtime -T "$since" 2>$null | Where-Object { $_ -match 'CircleAI.Soak.*ears:' })
$pick = if ($All) { $lines } else { $lines | Where-Object { $_ -match 'open in|finished in|spent|Exception' -or $_ -match 'update \d{4,} ms' } }
$pick | ForEach-Object { $_ -replace '^.*CircleAI\.Soak:?\s*', '' }
