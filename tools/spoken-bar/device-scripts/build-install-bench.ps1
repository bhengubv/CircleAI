# Signed Release build of the service WITHOUT LLVM, proven fresh, installed in place, then benched.
# Refuses to install if any AOT image predates this build or any LLVM object was produced;
# refuses to bench if the service crashes after the install.
$ErrorActionPreference = 'Continue'
$adb = "C:\Program Files (x86)\Android\android-sdk\platform-tools\adb.exe"
$msb = "C:\Program Files\Microsoft Visual Studio\18\Insiders\MSBuild\Current\Bin\amd64\MSBuild.exe"
$serial = "UTKDU19919000815"
$repo = "C:\Dev\Solutions\com.bhengubv\CircleAI"
$proj = "$repo\src\CircleAIService\CircleAIService.csproj"
$aotDir = "$repo\src\CircleAIService\obj\Release\net10.0-android\android-arm64\aot"
$apk = "$repo\src\CircleAIService\bin\Release\net10.0-android\android-arm64\com.bhengubv.circleai.service-Signed.apk"
$scratch = "."
$log = "$scratch\service-build.log"

$e = Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'ESENT'; Id = 508, 510; StartTime = (Get-Date).AddMinutes(-15) } -ErrorAction SilentlyContinue
if (@($e).Count -gt 0) { "STALLS: $(@($e).Count) - not building"; exit 1 }

$start = Get-Date
$ErrorActionPreference = 'Stop'
. "C:\Dev\Solutions\com.bhengubv\thegeeknetwork\Deployment\deployment-credentials.ps1" *> $null
$sc = if ($SIGNING_CREDENTIALS) { $SIGNING_CREDENTIALS } else { $Script:SIGNING_CREDENTIALS }
$pw = $sc.ANDROID_KEYSTORE_PASSWORD
$ErrorActionPreference = 'Continue'
if (-not $pw) { "no signing password loaded - not building"; exit 1 }
$pwArg = $pw.Replace('%', '%25').Replace(';', '%3B')
& $msb $proj -t:SignAndroidPackage -low -m:1 -nodeReuse:false -p:UseSharedCompilation=false -p:Configuration=Release -p:TargetFramework=net10.0-android -p:RuntimeIdentifier=android-arm64 -p:AndroidPackageFormat=apk "-p:AndroidSigningKeyPass=$pwArg" "-p:AndroidSigningStorePass=$pwArg" -v:m 2>&1 |
    ForEach-Object { "$_".Replace($pwArg, '***').Replace($pw, '***') } | Set-Content -Path $log -Encoding utf8
$code = $LASTEXITCODE
Remove-Variable pw, pwArg, sc -ErrorAction SilentlyContinue

"SERVICE BUILD EXIT $code after $([int]((Get-Date) - $start).TotalMinutes) min"
Select-String -Path $log -Pattern ' error |CIRCLEAI0' | Select-Object -Last 8 | ForEach-Object { $_.Line }
$llvmObjects = @(Select-String -Path $log -Pattern 'llvm\.o').Count
$aotLines = @(Select-String -Path $log -Pattern '\[\d+/\d+\] .+\.dll -> ').Count
"AOT progress lines: $aotLines; LLVM objects named in the log: $llvmObjects"
$stalls = @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'ESENT'; Id = 508, 510; StartTime = $start } -ErrorAction SilentlyContinue)
"disk stalls during the build: $($stalls.Count)"
if ($code -ne 0) { exit 1 }

$so = @(Get-ChildItem $aotDir -Filter *.so)
$stale = @($so | Where-Object { $_.LastWriteTime -lt $start })
"AOT images: $($so.Count), older than this build: $($stale.Count)"
if ($so.Count -eq 0 -or $stale.Count -gt 0 -or $llvmObjects -gt 0) {
    $stale | Select-Object -First 5 | ForEach-Object { "  stale: $($_.Name) $($_.LastWriteTime)" }
    "NOT INSTALLING: stale or LLVM AOT images"; exit 1
}

$apkItem = Get-Item $apk
"apk: $($apkItem.Length) bytes, written $($apkItem.LastWriteTime)"
& $adb -s $serial install -r --no-incremental $apk 2>&1 | Where-Object { $_ -match 'Success|Failure' }
$t0 = "$(& $adb -s $serial shell "date '+%m-%d %H:%M:%S.000'" 2>$null)".Trim()
& $adb -s $serial shell "dumpsys package com.bhengubv.circleai.service | grep -E 'lastUpdateTime|firstInstallTime' | head -2" 2>$null
"installed at device time $t0"

$svcPid = ''
for ($i = 0; $i -lt 60; $i++) {
    $svcPid = "$(& $adb -s $serial shell "pidof com.bhengubv.circleai.service" 2>$null)".Trim()
    if ($svcPid) { break }
    Start-Sleep -Seconds 10
}
if (-not $svcPid) { "service did not come up within 10 min - not benching"; exit 1 }
"service up: pid $svcPid after ~$($i * 10) s; watching 45 s for crashes"
Start-Sleep -Seconds 45
$fatal = @(& $adb -s $serial logcat -d -T "$t0" 2>$null | Select-String -Pattern 'Fatal signal|Abort message')
$svcPid = "$(& $adb -s $serial shell "pidof com.bhengubv.circleai.service" 2>$null)".Trim()
"pid now: [$svcPid]; fatal lines since install: $($fatal.Count)"
if ($fatal.Count -gt 0 -or -not $svcPid) { $fatal | Select-Object -First 6 | ForEach-Object { $_.Line }; "UNSTABLE - not benching"; exit 1 }

$t1 = "$(& $adb -s $serial shell "date '+%m-%d %H:%M:%S.000'" 2>$null)".Trim()
& $adb -s $serial shell "am broadcast -a com.bhengubv.circleai.action.SOAK -n com.bhengubv.circleai.service/ai.circle.SoakReceiver --es do bench" 2>$null | Select-Object -Last 1
$lines = @()
for ($i = 0; $i -lt 36; $i++) {
    Start-Sleep -Seconds 5
    $lines = @(& $adb -s $serial logcat -d -T "$t1" 2>$null | Select-String -Pattern 'CircleAI\.(Engine\.Bench|Bench|Soak)|Fatal signal|Abort message')
    if (@($lines | Where-Object { $_.Line -match 'bench done|Fatal signal' }).Count -gt 0) { break }
}
$lines | ForEach-Object { $_.Line } | Tee-Object -FilePath "$scratch\p30-bench-4.log"
