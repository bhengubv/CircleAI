# The fast-kernel probe on the P30, inside Circle AI's service: build on CoreCLR, install in place, run it.
# No screen touched, no device setting changed, nothing downloaded.
param([string]$Serial = '192.168.0.115:5555')
$ErrorActionPreference = 'Continue'
$s = '.'
$repo = 'C:\Dev\Solutions\com.bhengubv\CircleAI'
$adb = 'C:\Program Files (x86)\Android\android-sdk\platform-tools\adb.exe'
$pkg = 'com.bhengubv.circleai.service'
$apk = "$repo\src\CircleAIService\bin\Release\net10.0-android\android-arm64\$pkg-Signed.apk"

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
& $adb -s $Serial install -r --no-incremental $item.FullName 2>&1 | Where-Object { $_ -match 'Success|Failure' }
$svc = ''
for ($i = 0; $i -lt 18; $i++) {
    Start-Sleep -Seconds 10
    $svc = "$(& $adb -s $Serial shell "pidof $pkg" 2>$null)".Trim()
    if ($svc) { break }
}
if (-not $svc) { "the service did not come up"; exit 1 }
"service up: pid $svc; settling 45 s"
Start-Sleep -Seconds 45

"######## fast $(Get-Date -Format HH:mm:ss)"
& "$s\p30-s9.ps1" -Serial $Serial -Tag '-fast' -Steps fast
"######## finished $(Get-Date -Format HH:mm:ss)"
