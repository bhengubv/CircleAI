# Deprecate every retired CircleAI.* id on nuget.org, and flag the broken 3.8.0.
#
# From 3.8.0 the product is ONE package. The 174 ids published at 3.7.0 and the 9
# retired before it are not republished under a new name - nothing a consumer
# references breaks - they simply stop moving. Left alone they would freeze at their
# last version with no signal that the code went anywhere.
#
# WHY THIS DRIVES nuget-plc AND NOT Invoke-WebRequest. An earlier version of this
# file hand-rolled PUT /api/v2/package/{id}/deprecations. The request was correct -
# Knapcode.PackageLifeCycle sends a byte-identical one - but hand-rolling it meant
# owning version discovery, existing-metadata checks, rate-limit backoff and the
# redaction of the key from the log, all of which the tool already does. The tool is
# by a NuGet team member and is the only supported route today: the official docs
# cover the website UI only, and NuGetGallery issue #8873 ("Add REST API to
# deprecate packages") is still open. The endpoint is a preview API.
#
#   dotnet tool install Knapcode.PackageLifeCycle --prerelease --global
#
# THE KEY NEEDS TWO THINGS, AND THEY ARE SEPARATE. Scope must include "Unlist or
# relist package versions" - push alone gives 403. The key's package GLOB PATTERN
# must also match: `CircleAI` matches only the exact id, so `CircleAI.Accessibility`
# is refused with "does not have permission to access the specified package". That
# message arrives in the HTTP STATUS LINE; the body is a generic IIS page, so the
# same failure looks like two different ones depending on which you read.
#
# --listed-verb stays Unchanged for the retired ids: deprecating is a signal, not a
# withdrawal, and they stay installable so existing builds keep working. 3.8.0 is
# the one exception - see PHASE 2.

param(
    [int]    $Limit = 0,        # 0 = all; set to 1 first to prove the key is accepted
    [switch] $DryRun,
    [switch] $SkipRetiredIds,   # only do PHASE 2
    [switch] $SkipBroken380     # only do PHASE 1
)

$ErrorActionPreference = 'Stop'

$ALTERNATE = 'CircleAI'
$MESSAGE   = 'This package now ships inside CircleAI. Reference CircleAI instead - it carries the same code in one assembly. See https://github.com/bhengubv/CircleAI/releases/tag/v3.8.1'

if (-not (Get-Command nuget-plc -ErrorAction SilentlyContinue)) {
    throw 'nuget-plc not found. Install: dotnet tool install Knapcode.PackageLifeCycle --prerelease --global'
}

. 'C:\Dev\Solutions\com.bhengubv\thegeeknetwork\Deployment\deployment-credentials.ps1' | Out-Null
$key = $NUGET_CREDENTIALS.API_KEY
if (-not $key) { throw 'NUGET_CREDENTIALS.API_KEY is empty' }

# Never let the key reach the transcript, whatever the tool decides to print.
function Invoke-Plc {
    param([string[]] $PlcArgs)
    $out = & nuget-plc @PlcArgs --api-key $key 2>&1
    $out | ForEach-Object { $_ -replace [regex]::Escape($key), '<redacted>' }
    return $LASTEXITCODE
}

$done = 0; $failed = @{}

# ── PHASE 1 — the retired ids ────────────────────────────────────────────────
if (-not $SkipRetiredIds) {
    # Discovered from the feed rather than from a list in this file: a hardcoded
    # list is how pack-3.6.0.ps1 came to cover 59 of 174 projects.
    Write-Host 'Enumerating CircleAI* ids owned by bhengubv...'
    $ids = New-Object System.Collections.Generic.List[string]
    for ($skip = 0; $skip -lt 500; $skip += 100) {
        $s = Invoke-RestMethod "https://azuresearch-usnc.nuget.org/query?q=CircleAI&prerelease=true&take=100&skip=$skip" -TimeoutSec 30
        if (-not $s.data) { break }
        foreach ($d in $s.data) {
            if ($d.owners -contains 'bhengubv' -and $d.id -like 'CircleAI*' -and $d.id -ne $ALTERNATE) {
                $ids.Add($d.id)
            }
        }
    }
    $ids = @($ids | Sort-Object -Unique)
    Write-Host "  found $($ids.Count) id(s) ($ALTERNATE itself is excluded by name)"
    if ($Limit -gt 0) { $ids = @($ids | Select-Object -First $Limit); Write-Host "  limited to $($ids.Count)" }

    $i = 0
    foreach ($id in $ids) {
        $i++
        # --overwrite so a re-run after a partial sweep is safe rather than 183 errors.
        $a = @('deprecate', $id, '--all', '--legacy',
               '--alternate-id', $ALTERNATE, '--message', $MESSAGE,
               '--overwrite', '--log-level', 'Warning')
        if ($DryRun) { $a += '--dry-run' }

        $code = Invoke-Plc $a
        if ($code -eq 0) {
            $done++
            Write-Host ("  {0,3}/{1}  {2}" -f $i, $ids.Count, $id)
        } else {
            $failed[$id] = "nuget-plc exit $code"
            Write-Host ("  {0,3}/{1}  {2}  FAILED" -f $i, $ids.Count, $id)
        }
        Start-Sleep -Milliseconds 400   # nuget.org rate-limits; 183 ids back to back trips it
    }
}

# ── PHASE 2 — CircleAI 3.8.0, which is broken rather than retired ────────────
# 3.8.0 carried a transitive Microsoft.AspNetCore.App FrameworkReference, so every
# android consumer fails NETSDK1082. It cannot be deleted from the feed. Unlike the
# retired ids this one IS a withdrawal: --critical-bugs, pointed at 3.8.1, and
# unlisted so no new consumer resolves to it.
if (-not $SkipBroken380) {
    Write-Host ''
    Write-Host 'CircleAI 3.8.0 - critical bugs, unlist, point at 3.8.1'
    $a = @('deprecate', $ALTERNATE, '--version', '3.8.0', '--critical-bugs',
           '--alternate-id', $ALTERNATE, '--alternate-version', '3.8.1',
           '--message', 'Breaks every net10.0-android consumer with NETSDK1082 (a transitive Microsoft.AspNetCore.App FrameworkReference). Use 3.8.1 or later.',
           '--listed-verb', 'Unlist', '--overwrite', '--log-level', 'Information')
    if ($DryRun) { $a += '--dry-run' }

    $code = Invoke-Plc $a
    if ($code -eq 0) { Write-Host '  done' } else { $failed['CircleAI 3.8.0'] = "nuget-plc exit $code" }
}

Write-Host ''
Write-Host "DEPRECATED: $done"
Write-Host "FAILED:     $($failed.Count)"
$failed.GetEnumerator() | Select-Object -First 20 | ForEach-Object { Write-Host "  $($_.Key): $($_.Value)" }
if ($failed.Count -gt 0) { exit 1 }
