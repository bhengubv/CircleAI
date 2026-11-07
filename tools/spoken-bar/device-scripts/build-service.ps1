# Signed Release build of the service WITHOUT LLVM - the Android head, so Android-only code is
# compiled (the test legs skip it). Builds only: no install, no phone needed. The whole build output
# goes to a log; its errors are picked out. Refuses to build after a disk stall.
$ErrorActionPreference = 'Continue'
$msb = "C:\Program Files\Microsoft Visual Studio\18\Insiders\MSBuild\Current\Bin\amd64\MSBuild.exe"
$repo = "C:\Dev\Solutions\com.bhengubv\CircleAI"
$proj = "$repo\src\CircleAIService\CircleAIService.csproj"
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
@(Select-String -Path $log -Pattern ': error ' | Select-Object -ExpandProperty Line -Unique) | Select-Object -First 20
$llvmObjects = @(Select-String -Path $log -Pattern 'llvm\.o').Count
"LLVM objects named in the log: $llvmObjects"
$stalls = @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'ESENT'; Id = 508, 510; StartTime = $start } -ErrorAction SilentlyContinue)
"disk stalls during the build: $($stalls.Count)"
exit $code
