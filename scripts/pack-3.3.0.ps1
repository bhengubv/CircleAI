# Pack all v3.3.0 packages into nupkgs/.
$ErrorActionPreference = 'Stop'
$root = 'C:\Dev\Solutions\com.bhengubv\CircleAI\src'
$nupkgs = 'C:\Dev\Solutions\com.bhengubv\CircleAI\nupkgs'
if (-not (Test-Path $nupkgs)) { New-Item -ItemType Directory -Path $nupkgs | Out-Null }
Get-ChildItem $nupkgs -Filter '*.nupkg' -ErrorAction SilentlyContinue | ForEach-Object { [IO.File]::Delete($_.FullName) }

$pkgs = @(
'CircleAI.AetherNet','CircleAI.AutonomousBiz','CircleAI.Banking','CircleAI.BuildFarm','CircleAI.CRM',
'CircleAI.CodeUnderstanding','CircleAI.Collaboration','CircleAI.Companion.Proactive','CircleAI.ContentPolicy','CircleAI.Core',
'CircleAI.DepBot','CircleAI.DevTools','CircleAI.Distribution','CircleAI.DocAnalytics','CircleAI.Domain',
'CircleAI.Embeddings.Local','CircleAI.Games','CircleAI.Hosting','CircleAI.Hosting.CloudFallback','CircleAI.Hosting.InferenceBridge',
'CircleAI.Hosting.Mcp','CircleAI.Hosting.Multiplayer','CircleAI.Inference','CircleAI.Inference.Server','CircleAI.Inference.Server.Enterprise',
'CircleAI.Inputs','CircleAI.Markets','CircleAI.MediaHub','CircleAI.MicroAgents','CircleAI.ModelAlignment',
'CircleAI.Observability','CircleAI.Observer','CircleAI.Operator','CircleAI.Pipelines','CircleAI.Plugins',
'CircleAI.Research','CircleAI.SDD','CircleAI.Search','CircleAI.Spatial','CircleAI.Speech',
'CircleAI.Speech.Cloud','CircleAI.Testing','CircleAI.Tools.Catalog','CircleAI.Video','CircleAI.Vision',
'CircleAI.Vision.Cloud','CircleAI.Visualization','CircleAI.WindowsAutomation','CircleAI.Workflows','CircleAI.SelfBench',
'CircleAI.Integration','CircleAI.Integration.Calendar','CircleAI.Integration.Email','CircleAI.Integration.News',
'CircleAI.Integration.Geo','CircleAI.Integration.HomeAssistant','CircleAI.Companion','CircleAI.Voice','CircleAI.Memory'
)
$dotnet = 'C:\Program Files\dotnet\dotnet.exe'
$ok = 0; $fail = @()
foreach ($p in $pkgs) {
    $csproj = Join-Path $root "$p\$p.csproj"
    if (-not (Test-Path $csproj)) { Write-Output "MISSING: $p"; continue }
    & $dotnet pack $csproj -c Release -o $nupkgs --nologo --verbosity quiet > $null 2>&1
    if ($LASTEXITCODE -eq 0) { $ok++; Write-Output "OK: $p" } else { $fail += $p; Write-Output "FAIL: $p exit=$LASTEXITCODE" }
}
Write-Output "TOTAL OK: $ok"
Write-Output "TOTAL FAIL: $($fail.Count)"
if ($fail.Count -gt 0) { Write-Output ('FAILED: ' + ($fail -join ',')) }
$count = (Get-ChildItem $nupkgs -Filter '*.nupkg' | Measure-Object).Count
Write-Output "NUPKGS COUNT: $count"
