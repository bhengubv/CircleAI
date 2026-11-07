# CircleAI.Tests on both legs through the build lock, one after the other. A disk stall makes the lock script refuse
# (exit 3): this waits a minute and asks again. A failing leg does not stop the next.
$ErrorActionPreference = 'Continue'
$lock = '.\locked-build.ps1'
$repo = 'C:\Dev\Solutions\com.bhengubv\CircleAI'

foreach ($leg in 'net10.0', 'net9.0') {
    "=== CircleAI.Tests $leg"
    do {
        & $lock -Repo $repo -Framework $leg -Filter 'FullyQualifiedName~CircleAI'
        $code = $LASTEXITCODE
        if ($code -eq 3) { Start-Sleep -Seconds 60 }
    } while ($code -eq 3)
}
"=== finished $(Get-Date -Format 'HH:mm:ss')"
