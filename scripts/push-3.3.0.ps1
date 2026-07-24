# Push all 3.3.0 nupkgs to GitHub Packages.
$ErrorActionPreference = 'Continue'
$nupkgs = 'C:\Dev\Solutions\com.bhengubv\CircleAI\nupkgs'
$dotnet = 'C:\Program Files\dotnet\dotnet.exe'

# Pull the bhengubv PAT from the configured NuGet source (already there).
$nuget_cfg = "$env:APPDATA\NuGet\NuGet.Config"
[xml]$cfg = Get-Content $nuget_cfg
$tok = $cfg.SelectSingleNode("/configuration/packageSourceCredentials/github/add[@key='ClearTextPassword']").value
if (-not $tok) { throw 'No GitHub Packages credentials in NuGet.Config' }

$pkgs = Get-ChildItem $nupkgs -Filter 'CircleAI.*.nupkg' | Where-Object { $_.Name -notmatch '\.symbols\.nupkg$' }
$ok = 0; $fail = @()
foreach ($p in $pkgs) {
    Write-Output "=== PUSH $($p.Name) ==="
    & $dotnet nuget push $p.FullName --source github --api-key $tok --skip-duplicate --no-symbols 2>&1 | Select-Object -Last 3
    if ($LASTEXITCODE -eq 0) { $ok++ } else { $fail += $p.Name }
}
Write-Output "TOTAL PUSHED: $ok"
Write-Output "TOTAL FAILED: $($fail.Count)"
if ($fail.Count -gt 0) { Write-Output ('FAILED: ' + ($fail -join ',')) }
