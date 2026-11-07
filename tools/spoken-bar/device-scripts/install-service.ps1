# Installs the signed Release service built by build-service.ps1, in place (models and data kept), on the P30.
# Refuses if any AOT image predates -Since or any LLVM object was built; then waits for the service, watches
# it 45 s for crashes, and prints what it said about itself at start-up.
param([Parameter(Mandatory)][datetime]$Since, [string]$Serial = "UTKDU19919000815")
$ErrorActionPreference = 'Continue'
$adb = "C:\Program Files (x86)\Android\android-sdk\platform-tools\adb.exe"
$serial = $Serial
$pkg = "com.bhengubv.circleai.service"
$repo = "C:\Dev\Solutions\com.bhengubv\CircleAI"
$aotDir = "$repo\src\CircleAIService\obj\Release\net10.0-android\android-arm64\aot"
$apk = "$repo\src\CircleAIService\bin\Release\net10.0-android\android-arm64\$pkg-Signed.apk"
$scratch = "."

$so = @(Get-ChildItem $aotDir -Filter *.so)
$stale = @($so | Where-Object { $_.LastWriteTime -lt $Since })
$llvm = @(Select-String -Path "$scratch\service-build.log" -Pattern 'llvm\.o').Count
"AOT images: $($so.Count), older than ${Since}: $($stale.Count); LLVM objects: $llvm"
if ($so.Count -eq 0 -or $stale.Count -gt 0 -or $llvm -gt 0) { "NOT INSTALLING: stale or LLVM AOT images"; exit 1 }
$apkItem = Get-Item $apk
if ($apkItem.LastWriteTime -lt $Since) { "NOT INSTALLING: the APK predates this build"; exit 1 }
"apk: $($apkItem.Length) bytes, written $($apkItem.LastWriteTime)"

"in front: " + "$(& $adb -s $serial shell "dumpsys window | grep -E 'mCurrentFocus' | head -1" 2>$null)".Trim()
$t0 = "$(& $adb -s $serial shell "date '+%m-%d %H:%M:%S.000'" 2>$null)".Trim()
& $adb -s $serial install -r --no-incremental $apk 2>&1 | Where-Object { $_ -match 'Success|Failure' }
& $adb -s $serial shell "dumpsys package $pkg | grep -E 'versionCode|lastUpdateTime|firstInstallTime' | head -3" 2>$null
"installed at device time $t0"

$svcPid = ''
for ($i = 0; $i -lt 60; $i++) {
    Start-Sleep -Seconds 10
    $svcPid = "$(& $adb -s $serial shell "pidof $pkg" 2>$null)".Trim()
    if ($svcPid) { break }
}
if (-not $svcPid) { "service did not come up within 10 min"; exit 1 }
"service up: pid $svcPid after ~$(($i + 1) * 10) s; watching 45 s for crashes"
Start-Sleep -Seconds 45
$lines = @(& $adb -s $serial logcat -d -T "$t0" 2>$null)
$fatal = @($lines | Select-String -Pattern 'Fatal signal|Abort message|FATAL EXCEPTION')
$svcPid = "$(& $adb -s $serial shell "pidof $pkg" 2>$null)".Trim()
"pid now: [$svcPid]; fatal lines since install: $($fatal.Count)"
$fatal | Select-Object -First 6 | ForEach-Object { $_.Line }
"--- what it said at start-up"
$said = @($lines | Select-String -Pattern 'CircleAI\.(Asks|Setup|Heal|Balance|Self|Engines|Housekeeping|Wake|Worker|Supervisor|Link)\b|I CircleAI ' | ForEach-Object { $_.Line })
$said | Select-Object -First 60
$said | Set-Content -Path "$scratch\p30-service-startup.log" -Encoding utf8
if ($fatal.Count -gt 0 -or -not $svcPid) { "UNSTABLE"; exit 1 }
