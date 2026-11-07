# Signed Release build of the service on CoreCLR instead of Mono: the same project and the same C#, with
# -p:UseMonoRuntime=false on the restore and the build. The restore fetches the CoreCLR packs into the NuGet cache
# (the owner's yes, 2026-11-05). The new assets file changes the hash the Android SDK keeps, so the SDK cleans the
# service's intermediate folder itself; the next Mono build needs a plain restore and does the same.
# READY-TO-RUN OFF: crossgen2 10.0.14 stops with "Target OS 'android' is not supported" under SDK 11 rc.3, so the
# phone JITs the C# instead. It starts slower; once a kernel is compiled its speed is the speed this trial measures.
$ErrorActionPreference = 'Continue'
$msb = "C:\Program Files\Microsoft Visual Studio\18\Insiders\MSBuild\Current\Bin\amd64\MSBuild.exe"
$repo = "C:\Dev\Solutions\com.bhengubv\CircleAI"
$proj = "$repo\src\CircleAIService\CircleAIService.csproj"
$scratch = "."
$log = "$scratch\service-coreclr-build.log"

$e = Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'ESENT'; Id = 508, 510; StartTime = (Get-Date).AddMinutes(-15) } -ErrorAction SilentlyContinue
if (@($e).Count -gt 0) { "STALLS: $(@($e).Count) - not building"; exit 1 }

$start = Get-Date
& $msb $proj -t:Restore -low -m:1 -nodeReuse:false -p:Configuration=Release -p:UseMonoRuntime=false -p:PublishReadyToRun=false -v:m -nologo 2>&1 | Set-Content -Path "$log.restore" -Encoding utf8
$code = $LASTEXITCODE
"RESTORE EXIT $code after $([int]((Get-Date) - $start).TotalSeconds) s"
@(Select-String -Path "$log.restore" -Pattern ': error ' | Select-Object -ExpandProperty Line -Unique) | Select-Object -First 12
if ($code -ne 0) { exit 1 }

$ErrorActionPreference = 'Stop'
. "C:\Dev\Solutions\com.bhengubv\thegeeknetwork\Deployment\deployment-credentials.ps1" *> $null
$sc = if ($SIGNING_CREDENTIALS) { $SIGNING_CREDENTIALS } else { $Script:SIGNING_CREDENTIALS }
$pw = $sc.ANDROID_KEYSTORE_PASSWORD
$ErrorActionPreference = 'Continue'
if (-not $pw) { "no signing password loaded - not building"; exit 1 }
$pwArg = $pw.Replace('%', '%25').Replace(';', '%3B')
& $msb $proj -t:SignAndroidPackage -low -m:1 -nodeReuse:false -p:UseSharedCompilation=false -p:Configuration=Release -p:TargetFramework=net10.0-android -p:RuntimeIdentifier=android-arm64 -p:AndroidPackageFormat=apk -p:UseMonoRuntime=false -p:PublishReadyToRun=false -p:TieredCompilation=false "-p:AndroidSigningKeyPass=$pwArg" "-p:AndroidSigningStorePass=$pwArg" -v:m 2>&1 |
    ForEach-Object { "$_".Replace($pwArg, '***').Replace($pw, '***') } | Set-Content -Path $log -Encoding utf8
$code = $LASTEXITCODE
Remove-Variable pw, pwArg, sc -ErrorAction SilentlyContinue

"SERVICE BUILD EXIT $code after $([int]((Get-Date) - $start).TotalMinutes) min"
@(Select-String -Path $log -Pattern ': error ' | Select-Object -ExpandProperty Line -Unique) | Select-Object -First 20
@(Select-String -Path $log -Pattern 'XA1040' | Select-Object -ExpandProperty Line -Unique) | Select-Object -First 2
$stalls = @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'ESENT'; Id = 508, 510; StartTime = $start } -ErrorAction SilentlyContinue)
"disk stalls during the build: $($stalls.Count)"
exit $code
