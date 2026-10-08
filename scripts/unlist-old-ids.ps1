# Unlist every retired CircleAI.* id on nuget.org.
#
# WHY UNLIST AND NOT DEPRECATE. Deprecation is the better signal - it carries a
# message and names the alternate package - but nuget.org has NO deprecation API:
# the service index advertises only PackagePublish/2.0.0 and no deprecation
# resource, and NuGetGallery issue #8873 ("Add REST API to deprecate packages") is
# still open. A key with the `*` glob and the "Unlist or relist" scope unlists
# happily (200) and is refused on /deprecations (403), which is what proves it is
# the API and not the key. Deprecating these would mean 183 visits to the website;
# scripts/deprecate-old-ids.ps1 records that route for whenever it is worth it.
#
# WHAT UNLISTING DOES AND DOES NOT DO. It hides the id from search and from the
# version picker. It does NOT break anything: an existing PackageReference to an
# exact version still restores, so no consumer's build stops working. That is the
# whole point - from 3.8.0 the product is ONE package, and these ids are not
# republished under a new name, they simply stop moving.
#
# The API is the documented one:
#   DELETE https://www.nuget.org/api/v2/package/{id}/{version}
#   X-NuGet-ApiKey: <key>
# It is per VERSION, not per id, so a 183-id sweep is several hundred calls and
# nuget.org rate-limits it. 429 and Retry-After are honoured.

param(
    [int]    $Limit = 0,              # 0 = all ids; set small first to prove the sweep
    [int]    $MaxWaitSeconds = 120,   # a longer Retry-After than this ends the run
    [switch] $DryRun
)

$ErrorActionPreference = 'Stop'

$KEEP     = 'CircleAI'      # the live package - must never be touched by this sweep
$ENDPOINT = 'https://www.nuget.org/api/v2/package'
$rateLimited = $false

. 'C:\Dev\Solutions\com.bhengubv\thegeeknetwork\Deployment\deployment-credentials.ps1' | Out-Null
$key = $NUGET_CREDENTIALS.API_KEY
if (-not $key) { throw 'NUGET_CREDENTIALS.API_KEY is empty' }

# Discovered from the feed rather than from a list in this file: a hardcoded list is
# how pack-3.6.0.ps1 came to cover 59 of 174 projects.
Write-Host 'Enumerating CircleAI* ids owned by bhengubv...'
$ids = New-Object System.Collections.Generic.List[string]
for ($skip = 0; $skip -lt 500; $skip += 100) {
    $s = Invoke-RestMethod "https://azuresearch-usnc.nuget.org/query?q=CircleAI&prerelease=true&take=100&skip=$skip" -TimeoutSec 30
    if (-not $s.data) { break }
    foreach ($d in $s.data) {
        if ($d.owners -contains 'bhengubv' -and $d.id -like 'CircleAI*' -and $d.id -ne $KEEP) {
            $ids.Add($d.id)
        }
    }
}
$ids = @($ids | Sort-Object -Unique)
Write-Host "  found $($ids.Count) id(s) ($KEEP itself is excluded by name)"
if ($Limit -gt 0) { $ids = @($ids | Select-Object -First $Limit); Write-Host "  limited to $($ids.Count)" }

$unlisted = 0; $calls = 0; $failed = @{}
$i = 0

foreach ($id in $ids) {
    $i++

    # Belt and braces on top of the enumeration filter. Unlisting the live package
    # is the one unrecoverable mistake this script could make.
    if ($id -eq $KEEP) { Write-Host "  REFUSING to touch $KEEP"; continue }

    try {
        $v = Invoke-RestMethod "https://api.nuget.org/v3-flatcontainer/$($id.ToLower())/index.json" -TimeoutSec 30
        $versions = @($v.versions)
    } catch {
        $failed["$id"] = 'could not list versions'
        continue
    }
    if ($versions.Count -eq 0) { continue }

    $idOk = 0
    foreach ($ver in $versions) {
        if ($rateLimited) { break }
        if ($DryRun) { $idOk++; $calls++; continue }

        $attempt = 0
        while ($true) {
            $attempt++
            $calls++
            try {
                $r = Invoke-WebRequest -Uri "$ENDPOINT/$id/$ver" -Method Delete `
                        -Headers @{ 'X-NuGet-ApiKey' = $key } `
                        -TimeoutSec 60 -SkipHttpErrorCheck

                if ($r.StatusCode -eq 429 -or ($r.StatusCode -eq 403 -and $r.Headers.'Retry-After')) {
                    $wait = 30
                    if ($r.Headers.'Retry-After') {
                        [int]::TryParse(($r.Headers.'Retry-After' | Select-Object -First 1), [ref]$wait) | Out-Null
                    }

                    # nuget.org's unlist budget is per HOUR, and when it is spent it says
                    # so with a Retry-After of nearly the whole window - 2938 s was the
                    # real one, measured after 68 ids. Sleeping that out five times over
                    # for a SINGLE version is hours of a held workstation for one package.
                    # So a long wait ends the sweep instead: re-run it later and it picks
                    # up where this left off, because an unlisted package drops out of the
                    # azuresearch query the enumeration above is built on. Short waits are
                    # ordinary throttling and are still slept through.
                    if ($wait -gt $MaxWaitSeconds) {
                        Write-Host ''
                        Write-Host "RATE LIMIT REACHED after $i id(s). nuget.org wants $wait s (~$([math]::Round($wait/60)) min)."
                        Write-Host 'Nothing is lost - re-run this script after that and it resumes.'
                        $script:rateLimited = $true
                        break
                    }

                    if ($attempt -gt 5) { $failed["$id/$ver"] = "rate limited after $attempt attempts"; break }
                    Write-Host "    throttled - waiting $wait s"
                    Start-Sleep -Seconds $wait
                    continue
                }

                if ($r.StatusCode -ge 200 -and $r.StatusCode -lt 300) { $idOk++; $unlisted++ }
                else { $failed["$id/$ver"] = "HTTP $([int]$r.StatusCode) $($r.StatusDescription)" }
                break
            } catch {
                if ($attempt -gt 3) { $failed["$id/$ver"] = $_.Exception.Message; break }
                Start-Sleep -Seconds 10
            }
        }
        Start-Sleep -Milliseconds 300
    }

    Write-Host ("  {0,3}/{1}  {2,-40} {3}/{4} version(s)" -f $i, $ids.Count, $id, $idOk, $versions.Count)

    if ($rateLimited) { break }
}

Write-Host ''
Write-Host "IDS REACHED:       $i of $($ids.Count)"
Write-Host "VERSIONS UNLISTED: $unlisted"
Write-Host "HTTP CALLS:        $calls"
Write-Host "FAILED:            $($failed.Count)"
$failed.GetEnumerator() | Select-Object -First 20 | ForEach-Object { Write-Host "  $($_.Key): $($_.Value)" }

if ($rateLimited) {
    Write-Host ''
    Write-Host 'STOPPED ON THE RATE LIMIT, NOT FINISHED. Re-run to continue.'
    exit 2
}
if ($failed.Count -gt 0) { exit 1 }
Write-Host ''
Write-Host 'SWEEP COMPLETE.'
