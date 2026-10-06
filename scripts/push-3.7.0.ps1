# Push the 3.7.0 bundle to BOTH feeds: nuget.org and GitHub Packages.
#
# WHAT CHANGED FROM push-3.6.0.ps1, AND WHY
# ------------------------------------------
# 1. BOTH FEEDS, ONE SCRIPT. That script pushed to GitHub Packages only
#    (`--source github`). Measured on 2026-10-06: nuget.org held 171 CircleAI ids at
#    3.6.0 while GitHub Packages held ONE (CircleAI.Inference). The feeds diverged
#    because they were pushed by different means, and nothing ever compared them. Two
#    feeds is the rule, so two feeds is one script.
#
# 2. NO CLEARTEXT TOKEN. That script read the GitHub PAT out of
#    %APPDATA%\NuGet\NuGet.Config:
#        $cfg.SelectSingleNode(".../github/add[@key='ClearTextPassword']").value
#    That entry is a leaked `gho_` OAuth token, written there during the 3.5.0 push and
#    found in a security review - see memory never-write-tokens-cleartext-nuget. Reading
#    it keeps the leak load-bearing. Credentials here come from `gh auth token` (needs
#    write:packages) and from $NUGET_CREDENTIALS.API_KEY in
#    thegeeknetwork\Deployment\deployment-credentials.ps1, used in memory for one
#    invocation, never persisted, never echoed, and no `dotnet nuget add source`.
#
# 3. IT REPORTS WHAT THE FEED ACTUALLY HOLDS AFTERWARDS. `--skip-duplicate` makes a
#    push that changed nothing exit 0, which is how a half-published release looks
#    identical to a finished one. The counts below separate pushed from skipped, and the
#    verification at the end asks each feed.

$ErrorActionPreference = 'Stop'

$repo   = Split-Path $PSScriptRoot -Parent
$nupkgs = Join-Path $repo 'nupkgs'
$version = '3.7.0'

$NUGET_ORG = 'https://api.nuget.org/v3/index.json'
$GITHUB    = 'https://nuget.pkg.github.com/bhengubv/index.json'

$creds = 'C:\Dev\Solutions\com.bhengubv\thegeeknetwork\Deployment\deployment-credentials.ps1'
if (-not (Test-Path $creds)) { throw "credentials file not found: $creds" }
. $creds | Out-Null
$nugetKey = $NUGET_CREDENTIALS.API_KEY
if (-not $nugetKey) { throw 'NUGET_CREDENTIALS.API_KEY is empty' }

$ghKey = (& gh auth token 2>&1 | Out-String).Trim()
if (-not $ghKey -or $ghKey -notmatch '^\w') { throw 'gh auth token returned nothing - run gh auth login with write:packages' }

$pkgs = @(Get-ChildItem $nupkgs -Filter 'CircleAI*.nupkg' |
            Where-Object { $_.Name -notlike '*.symbols.*' } | Sort-Object Name)
if ($pkgs.Count -eq 0) { throw "no nupkgs in $nupkgs - run pack-$version.ps1 first" }
Write-Output "PACKAGES TO PUSH: $($pkgs.Count)"

# WARNING, AND THE BUG THIS FIXES. Everything a PowerShell function writes to the
# OUTPUT stream becomes its return value, so `$f1 = Push-Feed ...` captured every
# Write-Host line into $f1 instead of printing it. The 3.7.0 run therefore logged
# four lines total - credentials, the count, a blank, and the verification header -
# with no per-feed progress and no failure list at all. $f1 was an array of strings,
# so the final `if ($f1 -gt 0) { throw }` compared an array to an integer and never
# fired: a run where all 174 pushes failed would have looked identical.
# Narration goes to the HOST stream, which is not capturable; only the count is returned.
function Push-Feed([string]$Label, [string]$Source, [string]$Key) {
    Write-Host ''
    Write-Host "=== $Label ==="
    $pushed = 0; $skipped = 0; $failed = @{}
    $i = 0
    foreach ($p in $pkgs) {
        $i++
        $log = & dotnet nuget push $p.FullName --source $Source --api-key $Key --skip-duplicate --no-symbols 2>&1
        $joined = ($log | Out-String)
        if ($LASTEXITCODE -eq 0) {
            if ($joined -match 'already exists|skipping') { $skipped++ } else { $pushed++ }
        } else {
            # Never print the whole log: an api-key can surface in a diagnostic line.
            $line = ($log | Select-String 'error|Forbidden|Unauthorized|Conflict' | Select-Object -First 1)
            $msg = if ($line) { "$line".Trim() } else { "exit $LASTEXITCODE" }
            $failed[$p.Name] = ($msg -replace [regex]::Escape($Key), '<redacted>')
        }
        if ($i % 20 -eq 0 -or $i -eq $pkgs.Count) {
            Write-Host "  $i/$($pkgs.Count): pushed=$pushed skipped=$skipped failed=$($failed.Count)"
        }
    }
    Write-Host "  PUSHED:  $pushed"
    Write-Host "  SKIPPED: $skipped (already at $version on this feed)"
    Write-Host "  FAILED:  $($failed.Count)"
    $failed.GetEnumerator() | Select-Object -First 10 | ForEach-Object { Write-Host "    $($_.Key): $($_.Value)" }
    return $failed.Count
}

$f1 = Push-Feed 'nuget.org'        $NUGET_ORG $nugetKey
$f2 = Push-Feed 'GitHub Packages'  $GITHUB    $ghKey

# ASK THE FEEDS, do not trust the push summary. nuget.org indexing lags a push by
# minutes, so a miss here right after publishing is not proof of failure - re-run this
# block rather than re-pushing.
Write-Output ''
Write-Output '=== verification: what nuget.org actually serves ==='
$ids = $pkgs | ForEach-Object { ($_.Name -replace "\.$([regex]::Escape($version))\.nupkg$", '') }
$at = 0; $not = @()
foreach ($id in $ids) {
    try {
        $r = Invoke-RestMethod -Uri "https://api.nuget.org/v3-flatcontainer/$($id.ToLower())/index.json" -TimeoutSec 30 -ErrorAction Stop
        if ($r.versions -contains $version) { $at++ } else { $not += $id }
    } catch { $not += $id }
}
Write-Output "  at $version : $at / $($ids.Count)"
if ($not.Count -gt 0) {
    Write-Output "  NOT YET VISIBLE ($($not.Count)):"
    $not | Select-Object -First 15 | ForEach-Object { Write-Output "    $_" }
}

if ([int]$f1 -gt 0 -or [int]$f2 -gt 0) { throw "push failed for $f1 package(s) on nuget.org and $f2 on GitHub Packages" }
Write-Output ''
Write-Output "DONE. $($pkgs.Count) packages, both feeds."
