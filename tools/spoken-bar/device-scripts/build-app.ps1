# Signed Release build of the thin app (com.bhengubv.circleai.hybrid), arm64, light - the same shape as
# build-service.ps1. Builds only. The whole output goes to a log; errors are picked out. Refuses after a disk stall.
$ErrorActionPreference = 'Continue'
$msb = "C:\Program Files\Microsoft Visual Studio\18\Insiders\MSBuild\Current\Bin\amd64\MSBuild.exe"
$repo = "C:\Dev\Solutions\com.bhengubv\CircleAI"
$proj = "$repo\samples\CircleAI.Samples.Hybrid\CircleAI.Samples.App\CircleAI.Samples.App.csproj"
$scratch = "."
$log = "$scratch\app-build.log"

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

"APP BUILD EXIT $code after $([int]((Get-Date) - $start).TotalMinutes) min"
@(Select-String -Path $log -Pattern ': error ' | Select-Object -ExpandProperty Line -Unique) | Select-Object -First 20
$stalls = @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'ESENT'; Id = 508, 510; StartTime = $start } -ErrorAction SilentlyContinue)
"disk stalls during the build: $($stalls.Count)"
$apk = Get-Item "$repo\samples\CircleAI.Samples.Hybrid\CircleAI.Samples.App\bin\Release\net10.0-android\android-arm64\com.bhengubv.circleai.hybrid-Signed.apk" -ErrorAction SilentlyContinue
if ($apk) { "apk: $($apk.Length) bytes, written $($apk.LastWriteTime)" }
exit $code
