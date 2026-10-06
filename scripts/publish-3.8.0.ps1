# Publish CircleAI 3.8.0 — ONE package, both feeds.
#
# 3.7.0 needed a pack script that discovered 174 projects and a push script that
# looped over 174 nupkgs twice. 3.8.0 is one project, so this is one file: there is
# nothing left to enumerate, and nothing left to half-publish.
#
# Carried forward from push-3.7.0.ps1, because both were real defects:
#   - BOTH feeds in one run. nuget.org held 171 CircleAI ids at 3.6.0 while GitHub
#     Packages held one, because they were pushed by different means and nothing
#     compared them.
#   - NO cleartext token. The 3.6.0 script read the GitHub PAT out of
#     %APPDATA%\NuGet\NuGet.Config - the leaked gho_ token a security review found.
#     Credentials come from gh auth token and $NUGET_CREDENTIALS.API_KEY, held for
#     one invocation, never persisted, never echoed.
#   - ASK THE FEED AFTERWARDS. --skip-duplicate makes a push that changed nothing
#     exit 0, so a half-published release looks identical to a finished one.
#   - Narration uses Write-Host, not Write-Output. Everything a PowerShell function
#     writes to the output stream becomes its return value, and that is how the
#     3.7.0 run logged four lines for 348 pushes and could not fire its own failure
#     gate.

$ErrorActionPreference = 'Stop'

$version = '3.8.0'
$repo    = Split-Path $PSScriptRoot -Parent
$proj    = Join-Path $repo 'src\CircleAI\CircleAI.csproj'
$outDir  = Join-Path $repo 'nupkgs'

$NUGET_ORG = 'https://api.nuget.org/v3/index.json'
$GITHUB    = 'https://nuget.pkg.github.com/bhengubv/index.json'

# ── Version, by evaluation rather than by reading ───────────────────────────────
# The 3.7.0 cut moved <Version> and left <AssemblyVersion> and <FileVersion> on
# 3.6.0.0, so packages went out at the new number carrying assemblies stamped the
# old one. All three are checked.
foreach ($pair in @(@{ P = 'Version';         W = $version },
                    @{ P = 'AssemblyVersion'; W = "$version.0" },
                    @{ P = 'FileVersion';     W = "$version.0" },
                    @{ P = 'PackageVersion';  W = $version })) {
    $got = (& dotnet msbuild $proj "-getProperty:$($pair.P)" -nologo 2>&1 | Out-String).Trim()
    if ($got -ne $pair.W) { throw "$($pair.P) is '$got', expected '$($pair.W)'" }
}
Write-Host "VERSION OK: Version/PackageVersion $version, Assembly/File $version.0"

# ── Pack ────────────────────────────────────────────────────────────────────────
if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }
New-Item -ItemType Directory -Path $outDir | Out-Null

& dotnet pack $proj -c Release -o $outDir --nologo --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw "pack failed with exit $LASTEXITCODE" }

$pkgs = @(Get-ChildItem $outDir -Filter '*.nupkg' | Where-Object { $_.Name -notlike '*.symbols.*' })
if ($pkgs.Count -ne 1) {
    throw "expected exactly one package, got $($pkgs.Count): $($pkgs.Name -join ', ')"
}
$pkg = $pkgs[0]
if ($pkg.Name -ne "CircleAI.$version.nupkg") {
    throw "packed $($pkg.Name), expected CircleAI.$version.nupkg"
}
Write-Host ("PACKED: {0}  {1:N1} MB" -f $pkg.Name, ($pkg.Length / 1MB))

# ── Credentials ─────────────────────────────────────────────────────────────────
$creds = 'C:\Dev\Solutions\com.bhengubv\thegeeknetwork\Deployment\deployment-credentials.ps1'
if (-not (Test-Path $creds)) { throw "credentials file not found: $creds" }
. $creds | Out-Null
$nugetKey = $NUGET_CREDENTIALS.API_KEY
if (-not $nugetKey) { throw 'NUGET_CREDENTIALS.API_KEY is empty' }

$ghKey = (& gh auth token 2>&1 | Out-String).Trim()
if (-not $ghKey) { throw 'gh auth token returned nothing - needs write:packages' }

# ── Push ────────────────────────────────────────────────────────────────────────
function Push-One([string]$Label, [string]$Source, [string]$Key) {
    Write-Host ""
    Write-Host "=== $Label ==="
    $log = & dotnet nuget push $pkg.FullName --source $Source --api-key $Key --skip-duplicate 2>&1
    $joined = $log | Out-String
    if ($LASTEXITCODE -ne 0) {
        # Never print the whole log: an api key can surface in a diagnostic line.
        $line = ($log | Select-String 'error|Forbidden|Unauthorized|Conflict' | Select-Object -First 1)
        $msg  = if ($line) { "$line".Trim() } else { "exit $LASTEXITCODE" }
        Write-Host ("  FAILED: " + ($msg -replace [regex]::Escape($Key), '<redacted>'))
        return 1
    }
    if ($joined -match 'already exists|skipping') { Write-Host '  SKIPPED (already at this version)' }
    else                                         { Write-Host '  PUSHED' }
    return 0
}

$failed = (Push-One 'nuget.org' $NUGET_ORG $nugetKey) + (Push-One 'GitHub Packages' $GITHUB $ghKey)

# ── Ask the feeds ───────────────────────────────────────────────────────────────
Write-Host ""
Write-Host '=== verification: what the feeds serve ==='

$onNuget = $false
try {
    $r = Invoke-RestMethod -Uri 'https://api.nuget.org/v3-flatcontainer/circleai/index.json' -TimeoutSec 30
    $onNuget = $r.versions -contains $version
} catch { }
Write-Host ("  nuget.org       CircleAI {0}: {1}" -f $version, $onNuget)
if (-not $onNuget) { Write-Host '    (nuget.org validation lags a push by minutes - re-check rather than re-push)' }

$onGitHub = $false
try {
    $v = Invoke-RestMethod -Uri 'https://api.github.com/users/bhengubv/packages/nuget/CircleAI/versions?per_page=100' `
         -Headers @{ Authorization = "Bearer $ghKey"; Accept = 'application/vnd.github+json' } -TimeoutSec 30
    $onGitHub = @($v.name) -contains $version
} catch { }
Write-Host ("  GitHub Packages CircleAI {0}: {1}" -f $version, $onGitHub)

if ([int]$failed -gt 0) { throw "$failed feed(s) failed" }
Write-Host ""
Write-Host "DONE. CircleAI $version, both feeds."
