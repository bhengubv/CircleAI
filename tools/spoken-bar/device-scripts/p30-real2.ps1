# Attention on the pool and the three-second reading limit, on the P30: build on CoreCLR, install in place, then the same
# questions as before. No screen touched, no device setting changed, nothing downloaded.
param([string]$Serial = '192.168.0.115:5555', [string]$Tag = 'r2')
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
if (-not $svc) { PutBack 'the service did not come up in 3 minutes'; exit 1 }
"service up: pid $svc; watching 60 s for crashes and the brain's start"
Start-Sleep -Seconds 60
$lines = @(& $adb -s $Serial logcat -d -v threadtime -T "$t0" 2>$null)
$fatal = @($lines | Select-String -Pattern 'Fatal signal|Abort message|FATAL EXCEPTION')
$now = "$(& $adb -s $Serial shell "pidof $pkg" 2>$null)".Trim()
"pid now: [$now]; fatal lines since install: $($fatal.Count)"
$fatal | Select-Object -First 10 | ForEach-Object { $_.Line.Substring(0, [Math]::Min(260, $_.Line.Length)) }
$lines | Where-Object { $_ -match 'managed engine ready|repacked|brain ready|did not start|OutOfMemory' } | ForEach-Object { $_.Substring(0, [Math]::Min(280, $_.Length)) }
if ($fatal.Count -gt 0 -or -not $now -or $now -ne $svc) { PutBack 'it did not stay up'; exit 1 }

"######## tool question $(Get-Date -Format HH:mm:ss)"
& "$s\p30-s9.ps1" -Serial $Serial -Tag "-${Tag}1" -Steps tool-model
"######## plain question $(Get-Date -Format HH:mm:ss)"
& "$s\p30-s9.ps1" -Serial $Serial -Tag "-${Tag}2" -Steps asked
"######## tool question again $(Get-Date -Format HH:mm:ss)"
& "$s\p30-s9.ps1" -Serial $Serial -Tag "-${Tag}3" -Steps tool-model
"######## short questions $(Get-Date -Format HH:mm:ss)"
& "$s\p30-where.ps1" -Serial $Serial -Tag "${Tag}a" -Text 'What is the capital of Kenya? Answer in one sentence.' -Samples 10
& "$s\p30-where.ps1" -Serial $Serial -Tag "${Tag}b" -Text 'Name one kind of bird.' -Samples 10
& "$s\p30-where.ps1" -Serial $Serial -Tag "${Tag}c" -Text 'Say hello in one word.' -Samples 8
"pids at the end: main $("$(& $adb -s $Serial shell "pidof $pkg" 2>$null)".Trim()), first was $svc"
$kills = @(& $adb -s $Serial logcat -d -v threadtime -T "$t0" 2>$null | Select-String -Pattern 'Killing .*circleai\.service')
"EMUI kills of the service: $($kills.Count)"
$kills | Select-Object -First 4 | ForEach-Object { $_.Line.Substring(0, [Math]::Min(170, $_.Line.Length)) }
"######## finished $(Get-Date -Format HH:mm:ss)"
