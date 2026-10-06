# Pack every v3.7.0 package into nupkgs/.
#
# 3.7.0: the split - thin app + CircleAIService - and a phone that decides for itself.
# See CHANGELOG [3.7.0].
#
# WHAT CHANGED FROM pack-3.6.0.ps1, AND WHY
# ------------------------------------------
# That script carried a HARDCODED LIST OF 59 package names. The repo has 172 packable
# projects, so 113 were never packed by it and whatever reached nuget.org at 3.6.0 came
# from somewhere else. A list in a script drifts from the tree the moment a project is
# added; the tree is the only thing that cannot be out of date with itself.
#
# So the set is DISCOVERED, not declared: every src/CircleAI* directory holding a csproj
# of the same name. That is the same rule Toolbox\Publish-NuGet.ps1 uses, kept local so a
# fresh clone of this repo alone can reproduce the release.
#
# Pack output is kept and reported on failure. The 3.6.0 script sent it to
# `> $null 2>&1`, which leaves nothing to read when a pack fails.

$ErrorActionPreference = 'Stop'

$repo   = Split-Path $PSScriptRoot -Parent
$src    = Join-Path $repo 'src'
$nupkgs = Join-Path $repo 'nupkgs'

if (-not (Test-Path $nupkgs)) { New-Item -ItemType Directory -Path $nupkgs | Out-Null }
Get-ChildItem $nupkgs -Filter '*.nupkg' -ErrorAction SilentlyContinue |
    ForEach-Object { [IO.File]::Delete($_.FullName) }

# The version is NOT passed on the command line: Directory.Build.props is the single
# source of truth. Assert it instead, because a release packed at the wrong number is
# the one failure nobody notices until it is immutable on a public feed. All THREE
# properties are checked - a cut that moves <Version> and leaves <AssemblyVersion> and
# <FileVersion> behind produces 3.7.0 packages full of 3.6.0.0 assemblies.
$expected = '3.7.0'
$probe = Join-Path $src 'CircleAI.Core\CircleAI.Core.csproj'
foreach ($pair in @(@{ P = 'Version'; W = $expected },
                    @{ P = 'AssemblyVersion'; W = "$expected.0" },
                    @{ P = 'FileVersion'; W = "$expected.0" })) {
    $got = (& dotnet msbuild $probe "-getProperty:$($pair.P)" -nologo 2>&1 | Out-String).Trim()
    if ($got -ne $pair.W) { throw "$($pair.P) is '$got', expected '$($pair.W)' - fix Directory.Build.props before packing" }
}
Write-Output "VERSION OK: Version=$expected AssemblyVersion=$expected.0 FileVersion=$expected.0"

$projects = Get-ChildItem $src -Directory |
    Where-Object { $_.Name -like 'CircleAI.*' -or $_.Name -eq 'CircleAI' } |
    Where-Object { Test-Path (Join-Path $_.FullName "$($_.Name).csproj") } |
    Sort-Object Name
Write-Output "PROJECTS DISCOVERED: $($projects.Count)"

$ok = 0; $fail = @{}
foreach ($p in $projects) {
    $csproj = Join-Path $p.FullName "$($p.Name).csproj"
    $log = & dotnet pack $csproj -c Release -o $nupkgs --nologo --verbosity quiet 2>&1
    if ($LASTEXITCODE -eq 0) {
        $ok++
        Write-Output "OK: $($p.Name)"
    } else {
        $first = ($log | Select-String ': error ' | Select-Object -First 1)
        $fail[$p.Name] = if ($first) { "$first".Trim() } else { "exit $LASTEXITCODE" }
        Write-Output "FAIL: $($p.Name) -- $($fail[$p.Name])"
    }
}

$produced = @(Get-ChildItem $nupkgs -Filter '*.nupkg' | Where-Object { $_.Name -notlike '*.symbols.*' })
Write-Output ''
Write-Output "TOTAL OK:      $ok / $($projects.Count)"
Write-Output "TOTAL FAIL:    $($fail.Count)"
$fail.GetEnumerator() | ForEach-Object { Write-Output "  $($_.Key): $($_.Value)" }
Write-Output "NUPKGS COUNT:  $($produced.Count)"

# A project with IsPackable=false packs successfully and produces nothing, so OK and
# NUPKGS COUNT are allowed to differ - say so rather than leaving the gap unexplained.
if ($produced.Count -ne $ok) {
    Write-Output "NOTE: $($ok - $produced.Count) project(s) packed without emitting a nupkg (IsPackable=false)."
}

# Prove the artefacts carry the version, not just the filename.
$wrong = @($produced | Where-Object { $_.Name -notmatch "\.$([regex]::Escape($expected))\.nupkg$" })
if ($wrong.Count -gt 0) {
    Write-Output "VERSION MISMATCH in $($wrong.Count) nupkg(s):"
    $wrong | Select-Object -First 10 | ForEach-Object { Write-Output "  $($_.Name)" }
    throw 'packed artefacts do not all carry 3.7.0'
}
Write-Output "ALL $($produced.Count) NUPKGS CARRY $expected"
