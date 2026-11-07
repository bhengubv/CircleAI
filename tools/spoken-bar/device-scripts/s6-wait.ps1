param([Parameter(Mandatory)][string]$File, [int]$Minutes = 25)
# Waits until a locked-build output file says the lock was released, then prints the lines that matter.
$deadline = (Get-Date).AddMinutes($Minutes)
$announced = $false
while ((Get-Date) -lt $deadline) {
    if (Test-Path $File) {
        $text = Get-Content $File -Raw -ErrorAction SilentlyContinue
        if (-not $announced -and $text -match 'lock taken') { 'build has the lock'; $announced = $true }
        if ($text -match 'lock released') {
            $text -split "`n" | Where-Object { $_ -match 'lock|build exit|test exit|error|Passed!|Failed!|Total tests|Aborted|STALLS|GAVE UP' } | Select-Object -First 60
            exit 0
        }
    }
    Start-Sleep -Seconds 10
}
'still waiting after ' + $Minutes + ' minutes'
exit 1
