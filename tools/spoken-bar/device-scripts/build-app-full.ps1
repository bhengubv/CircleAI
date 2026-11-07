# Plain restore of the thin app (the service's CoreCLR restore leaves the shared projects in that mode), then its signed Release
# build through build-app.ps1. Builds only; the install is a separate step.
$ErrorActionPreference = 'Continue'
$msb = "C:\Program Files\Microsoft Visual Studio\18\Insiders\MSBuild\Current\Bin\amd64\MSBuild.exe"
$repo = "C:\Dev\Solutions\com.bhengubv\CircleAI"
$proj = "$repo\samples\CircleAI.Samples.Hybrid\CircleAI.Samples.App\CircleAI.Samples.App.csproj"
$scratch = "."
$start = Get-Date
& $msb $proj -t:Restore -low -m:1 -nodeReuse:false -p:Configuration=Release -v:m -nologo 2>&1 | Set-Content -Path "$scratch\app-restore.log" -Encoding utf8
"APP RESTORE EXIT $LASTEXITCODE after $([int]((Get-Date) - $start).TotalSeconds) s"
& "$scratch\build-app.ps1"
