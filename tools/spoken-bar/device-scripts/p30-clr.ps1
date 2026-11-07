# CoreCLR on the P30: build, install in place, watch for a crash loop and put the Mono build back if there is one,
# then the kernel bench and the same three questions as the Mono run. No screen touched, no device setting changed.
param([string]$Serial = '192.168.0.115:5555')
$ErrorActionPreference = 'Continue'
$s = '.'
$repo = 'C:\Dev\Solutions\com.bhengubv\CircleAI'
$adb = 'C:\Program Files (x86)\Android\android-sdk\platform-tools\adb.exe'
$pkg = 'com.bhengubv.circleai.service'
$apk = "$repo\src\CircleAIService\bin\Release\net10.0-android\android-arm64\$pkg-Signed.apk"
$good = "$s\known-good\service-mono-v62-signed.apk"

function PutBack([string]$why) {
    "PUTTING THE MONO BUILD BACK: $why"
    & $adb -s $Serial install -r --no-incremental $good 2>&1 | Where-Object { $_ -match 'Success|Failure' }
    Start-Sleep -Seconds 20
    "service now: [$("$(& $adb -s $Serial shell "pidof $pkg" 2>$null)".Trim())]"
}

$since = Get-Date
"######## restore and build on CoreCLR $(Get-Date -Format HH:mm:ss)"
do {
    # A disk stall in the last 15 minutes makes the lock script refuse (exit 3): wait a minute and ask again.
    & "$s\locked-build.ps1" -Repo $repo -Command "& '$s\build-service-coreclr.ps1'"
    $code = $LASTEXITCODE
    if ($code -eq 3) { Start-Sleep -Seconds 60 }
} while ($code -eq 3)

$item = Get-Item $apk -ErrorAction SilentlyContinue
if (-not $item -or $item.LastWriteTime -lt $since) { "NOT INSTALLING: no APK from this build"; exit 1 }
"apk: $($item.Length) bytes, written $($item.LastWriteTime)"
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::OpenRead($item.FullName)
$zip.Entries | Where-Object { $_.FullName -match '^lib/arm64-v8a/.*(coreclr|monosgen|mono-|net-android|netandroid|clr|xamarin-app|System\.Native)' } |
    ForEach-Object { "  $($_.FullName) $($_.Length)" }
$zip.Dispose()

"######## install $(Get-Date -Format HH:mm:ss)"
"in front: $("$(& $adb -s $Serial shell "dumpsys window | grep -E 'mCurrentFocus' | head -1" 2>$null)".Trim())"
$t0 = "$(& $adb -s $Serial shell "date '+%m-%d %H:%M:%S.000'" 2>$null)".Trim()
& $adb -s $Serial install -r --no-incremental $item.FullName 2>&1 | Where-Object { $_ -match 'Success|Failure' }
& $adb -s $Serial shell "dumpsys package $pkg | grep -E 'versionCode|lastUpdateTime|firstInstallTime' | head -3" 2>$null

$svc = ''
for ($i = 0; $i -lt 18; $i++) {
    Start-Sleep -Seconds 10
    $svc = "$(& $adb -s $Serial shell "pidof $pkg" 2>$null)".Trim()
    if ($svc) { break }
}
if (-not $svc) { PutBack 'the service did not come up in 3 minutes'; exit 1 }
"service up: pid $svc after ~$(($i + 1) * 10) s; watching 60 s for crashes"
Start-Sleep -Seconds 60
$lines = @(& $adb -s $Serial logcat -d -T "$t0" 2>$null)
$fatal = @($lines | Select-String -Pattern 'Fatal signal|Abort message|FATAL EXCEPTION')
$now = "$(& $adb -s $Serial shell "pidof $pkg" 2>$null)".Trim()
"pid now: [$now]; fatal lines since install: $($fatal.Count)"
$fatal | Select-Object -First 10 | ForEach-Object { $_.Line.Substring(0, [Math]::Min(260, $_.Line.Length)) }
$lines | Set-Content -Path "$s\p30-clr-startup.log" -Encoding utf8
if ($fatal.Count -gt 0 -or -not $now -or $now -ne $svc) { PutBack 'it did not stay up'; exit 1 }

"######## kernel bench $(Get-Date -Format HH:mm:ss)"
& "$s\p30-s9.ps1" -Serial $Serial -Tag '-clr' -Steps kernels
if (-not (Select-String -Path "$s\s9-kernels-clr.log" -Pattern 'bench done in' -Quiet)) { PutBack 'the bench did not finish'; exit 1 }

"######## tool question $(Get-Date -Format HH:mm:ss)"
& "$s\p30-s9.ps1" -Serial $Serial -Tag '-clr1' -Steps tool-model
"######## plain question $(Get-Date -Format HH:mm:ss)"
& "$s\p30-s9.ps1" -Serial $Serial -Tag '-clr2' -Steps asked
"######## tool question again $(Get-Date -Format HH:mm:ss)"
& "$s\p30-s9.ps1" -Serial $Serial -Tag '-clr3' -Steps tool-model
"######## finished $(Get-Date -Format HH:mm:ss)"
