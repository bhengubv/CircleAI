# Build on CoreCLR, install in place, wait for the brain, then run the recorded questions through the whole spoken turn and print
# the turn's own timing. No screen touched, no device setting changed, nothing downloaded. -NoBuild skips the build and install.
param([string]$Serial = 'UTKDU19919000815', [string[]]$Clips = @('q-kenya', 'q-bird', 'q-sky'), [switch]$NoBuild, [int]$Each = 45, [int]$Settle = 40, [string]$Extra = "")
$ErrorActionPreference = 'Continue'
$s = '.'
$repo = 'C:\Dev\Solutions\com.bhengubv\CircleAI'
$adb = 'C:\Program Files (x86)\Android\android-sdk\platform-tools\adb.exe'
$pkg = 'com.bhengubv.circleai.service'
$apk = "$repo\src\CircleAIService\bin\Release\net10.0-android\android-arm64\$pkg-Signed.apk"
$good = "$s\known-good\service-mono-v62-signed.apk"

function Soak([string]$args2) { & $adb -s $Serial shell "am broadcast -n $pkg/ai.circle.SoakReceiver -a com.bhengubv.circleai.action.SOAK $args2" 2>$null | Out-Null }

if (-not $NoBuild) {
    $since = Get-Date
    "######## build $(Get-Date -Format HH:mm:ss)"
    do {
        & "$s\locked-build.ps1" -Repo $repo -Command "& '$s\build-service-coreclr.ps1'"
        $code = $LASTEXITCODE
        if ($code -eq 3) { Start-Sleep -Seconds 60 }
    } while ($code -eq 3)
    $item = Get-Item $apk -ErrorAction SilentlyContinue
    if (-not $item -or $item.LastWriteTime -lt $since) { "NOT INSTALLING: no APK from this build"; exit 1 }
    "apk: $($item.Length) bytes, written $($item.LastWriteTime)"

    "######## install $(Get-Date -Format HH:mm:ss)"
    $t0 = "$(& $adb -s $Serial shell "date '+%m-%d %H:%M:%S.000'" 2>$null)".Trim()
    & $adb -s $Serial install -r --no-incremental $item.FullName 2>&1 | Where-Object { $_ -match 'Success|Failure' }
    $svc = ''
    for ($i = 0; $i -lt 18; $i++) {
        Start-Sleep -Seconds 10
        $svc = "$(& $adb -s $Serial shell "pidof $pkg" 2>$null)".Trim()
        if ($svc) { break }
    }
    if (-not $svc) { "THE SERVICE DID NOT COME UP"; & $adb -s $Serial install -r --no-incremental $good 2>&1 | Where-Object { $_ -match 'Success|Failure' }; exit 1 }
    "service up: pid $svc; waiting for the greeting, at most $Settle s"
    for ($w = 0; $w -lt $Settle; $w += 5) { Start-Sleep -Seconds 5; if (@(& $adb -s $Serial logcat -d -v threadtime -T "$t0" 2>$null | Select-String -Pattern "greeting warmed|greeting could not").Count -gt 0) { break } }
    "ready after $w s"
    Start-Sleep -Seconds 12
    $fatal = @(& $adb -s $Serial logcat -d -v threadtime -T "$t0" 2>$null | Select-String -Pattern 'Fatal signal|Abort message|FATAL EXCEPTION')
    "fatal lines since install: $($fatal.Count)"
    $fatal | Select-Object -First 8 | ForEach-Object { $_.Line.Substring(0, [Math]::Min(240, $_.Line.Length)) }
    if ($fatal.Count -gt 0) { "STOPPING: it did not stay up"; exit 1 }
}

"######## recorded questions $(Get-Date -Format HH:mm:ss)"
foreach ($clip in $Clips) {
    $since = "$(& $adb -s $Serial shell "date '+%m-%d %H:%M:%S.000'" 2>$null)".Trim()
    "---- $clip at $since"
    Soak "--es do talk --es file $clip.wav $Extra"
    Start-Sleep -Seconds $Each
    $lines = @(& $adb -s $Serial logcat -d -v threadtime -T "$since" 2>$null)
    $lines | Where-Object { $_ -match 'spoken turn:|stt: \d|tts: \d|link stt|prompt tokens|CircleAI\.WakeTurn|talk:|CIRCLEAI-TURN' -and $_ -notmatch 'kws: at' } |
        ForEach-Object { $_.Substring(19, [Math]::Min(230, $_.Length - 19)) }
}
"######## finished $(Get-Date -Format HH:mm:ss)"
