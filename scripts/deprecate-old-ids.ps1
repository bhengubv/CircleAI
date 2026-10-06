# Deprecate every retired CircleAI.* id on nuget.org, pointing each at CircleAI.
#
# From 3.8.0 the product is ONE package. The 174 ids published at 3.7.0 and the 9
# retired before it are not republished under a new name - nothing a consumer
# references breaks - they simply stop moving. Left alone they would freeze at their
# last version with no signal that the code went anywhere, and a consumer bumping to
# the current version would find no such version.
#
# .NET does not need pointer packages for this. nuget.org has a deprecation API that
# names an alternate package, and every consumer gets a restore warning pointing at
# it. That is the designed mechanism; pointer packages predate it.
#
# API (preview, same one Knapcode.PackageLifeCycle drives):
#   PUT https://www.nuget.org/api/v2/package/{id}/deprecations
#   X-NuGet-ApiKey: <key>
#   { versions[], isLegacy, hasCriticalBugs, isOther, alternatePackageId,
#     alternatePackageVersion, message, listedVerb }
# It rate-limits with 429 and with 403 + Retry-After, so both are honoured.
#
# listedVerb stays Unchanged: deprecating is a signal, not a withdrawal. The packages
# stay listed and installable, which is what keeps existing builds working.

param(
    [int] $Limit = 0,          # 0 = all; set to 1 first to prove the API takes it
    [switch] $DryRun
)

$ErrorActionPreference = 'Stop'

$ALTERNATE = 'CircleAI'
$MESSAGE   = 'This package now ships inside CircleAI. Reference CircleAI instead - it carries the same code in one assembly. See https://github.com/bhengubv/CircleAI/releases/tag/v3.8.0'
$ENDPOINT  = 'https://www.nuget.org/api/v2/package'

. 'C:\Dev\Solutions\com.bhengubv\thegeeknetwork\Deployment\deployment-credentials.ps1' | Out-Null
$key = $NUGET_CREDENTIALS.API_KEY
if (-not $key) { throw 'NUGET_CREDENTIALS.API_KEY is empty' }

# Discover the set from the feed rather than from a list in this file: a hardcoded
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
Write-Host "  found $($ids.Count) id(s) to deprecate (CircleAI itself is excluded by name)"
if ($Limit -gt 0) { $ids = @($ids | Select-Object -First $Limit); Write-Host "  limited to $($ids.Count)" }

$done = 0; $skipped = 0; $failed = @{}
$i = 0
foreach ($id in $ids) {
    $i++

    # Every version, because the whole id is retired - not just its newest.
    try {
        $v = Invoke-RestMethod "https://api.nuget.org/v3-flatcontainer/$($id.ToLower())/index.json" -TimeoutSec 30
        $versions = @($v.versions)
    } catch {
        $failed[$id] = 'could not list versions'
        continue
    }
    if ($versions.Count -eq 0) { $skipped++; continue }

    $body = @{
        versions           = $versions
        isLegacy           = $true
        hasCriticalBugs    = $false
        isOther            = $false
        alternatePackageId = $ALTERNATE
        message            = $MESSAGE
        listedVerb         = 'Unchanged'
    } | ConvertTo-Json -Depth 4

    if ($DryRun) {
        Write-Host ("  [dry] {0,-36} {1} version(s)" -f $id, $versions.Count)
        $done++
        continue
    }

    $attempt = 0
    while ($true) {
        $attempt++
        try {
            $r = Invoke-WebRequest -Uri "$ENDPOINT/$id/deprecations" -Method Put `
                    -Headers @{ 'X-NuGet-ApiKey' = $key } `
                    -ContentType 'application/json' -Body $body `
                    -TimeoutSec 60 -SkipHttpErrorCheck

            if ($r.StatusCode -eq 429 -or ($r.StatusCode -eq 403 -and $r.Headers.'Retry-After')) {
                $wait = 30
                if ($r.Headers.'Retry-After') {
                    [int]::TryParse(($r.Headers.'Retry-After' | Select-Object -First 1), [ref]$wait) | Out-Null
                }
                if ($attempt -gt 5) { $failed[$id] = "rate limited after $attempt attempts"; break }
                Write-Host "  rate limited on $id - waiting $wait s"
                Start-Sleep -Seconds $wait
                continue
            }

            if ($r.StatusCode -ge 200 -and $r.StatusCode -lt 300) {
                $done++
                Write-Host ("  {0,3}/{1}  {2,-36} {3} version(s)" -f $i, $ids.Count, $id, $versions.Count)
            } else {
                $failed[$id] = "HTTP $($r.StatusCode) $($r.StatusDescription)"
            }
            break
        } catch {
            if ($attempt -gt 3) { $failed[$id] = $_.Exception.Message; break }
            Start-Sleep -Seconds 10
        }
    }
}

Write-Host ''
Write-Host "DEPRECATED: $done"
Write-Host "SKIPPED:    $skipped (no versions on the feed)"
Write-Host "FAILED:     $($failed.Count)"
$failed.GetEnumerator() | Select-Object -First 15 | ForEach-Object { Write-Host "  $($_.Key): $($_.Value)" }
if ($failed.Count -gt 0) { exit 1 }
