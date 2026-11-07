# Both legs and the Android heads for the S5 merge, kind to the SMR disk: waits out a stall window,
# pauses between heavy steps, and retries a step the lock refused for a stall (up to four times).
$s = '.'
$r = 'C:\Dev\Solutions\com.bhengubv\CircleAI'
$steps = @(
    @{ Name = 'net10.0'; Args = @{ Repo = $r; Framework = 'net10.0'; Filter = 'FullyQualifiedName~CircleAI' } },
    @{ Name = 'net9.0';  Args = @{ Repo = $r; Framework = 'net9.0';  Filter = 'FullyQualifiedName~CircleAI' } },
    @{ Name = 'android'; Args = @{ Repo = $r; Android = $true } }
)
foreach ($step in $steps) {
    for ($try = 1; $try -le 4; $try++) {
        $splat = $step.Args
        $out = & "$s\locked-build.ps1" @splat 2>&1 | ForEach-Object { "$_" }
        $out | ForEach-Object { $_ }
        if (-not ($out -match 'DISK STALLS')) { break }
        "[$(Get-Date -Format HH:mm:ss)] $($step.Name): refused for a disk stall, waiting 5 min (try $try)"
        Start-Sleep -Seconds 300
    }
    "[$(Get-Date -Format HH:mm:ss)] $($step.Name) done; pausing 90 s for the disk"
    Start-Sleep -Seconds 90
}
"ALL DONE $(Get-Date -Format HH:mm:ss)"
