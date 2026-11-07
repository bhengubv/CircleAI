# Compiles the service's desktop head (CircleAIService for net10.0: Host\Program.cs) - the test legs
# do not reference it and the Android compile removes Host\**, so nothing else builds it. Run under
# locked-build.ps1 -Command.
$ErrorActionPreference = 'Continue'
$msb = "C:\Program Files\Microsoft Visual Studio\18\Insiders\MSBuild\Current\Bin\amd64\MSBuild.exe"
$proj = "C:\Dev\Solutions\com.bhengubv\CircleAI\src\CircleAIService\CircleAIService.csproj"
$log = ".\locked-logs\desktop-head-$(Get-Date -Format HHmmss).log"
& $msb $proj -t:Restore -low -m:1 -nodeReuse:false -p:Configuration=Debug -v:m -nologo 2>&1 | Set-Content -Path "$log.restore" -Encoding utf8
& $msb $proj -t:Compile -low -m:1 -nodeReuse:false -p:UseSharedCompilation=false -p:Configuration=Debug -p:TargetFramework=net10.0 -v:m -nologo 2>&1 | Set-Content -Path $log -Encoding utf8
"desktop head compile exit $LASTEXITCODE (log $log)"
@(Select-String -Path $log -Pattern ': error ' | Select-Object -ExpandProperty Line -Unique) | Select-Object -First 25
