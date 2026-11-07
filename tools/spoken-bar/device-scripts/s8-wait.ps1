# Waits up to -Seconds for my locked job to reach -Until (a regex over its output and its command log),
# then prints the key lines so far. Reads only; runs nothing heavy.
param([int]$Seconds = 540, [string]$Until = 'lock released|GAVE UP|DISK STALLS', [string]$Task = 'bbstw2zc6')
$scratch = 'C:\Users\tbeng\AppData\Local\Temp\claude\C--Dev-Solutions-com-bhengubv\3f97cffd-a2b4-467a-8e81-b51566f2dbd3'
$out = "$scratch\tasks\$Task.output"
$key = 'lock taken|lock released|GAVE UP|DISK STALLS|build exit|vision test exit|^== |test exit|Passed!|Failed!|Aborted|: error |command done|^\s+Failed '
$deadline = (Get-Date).AddSeconds($Seconds)
while ($true) {
    $text = @()
    if (Test-Path $out) { $text += Get-Content $out -ErrorAction SilentlyContinue }
    $log = Get-ChildItem "$scratch\scratchpad\locked-logs" -Filter 'agent-a5582f0c55c8a4162-*-command.log' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    $started = ($text -match 'lock taken').Count -gt 0
    if ($log -and $started -and $log.LastWriteTime -gt (Get-Item $out).CreationTime) { $text += Get-Content $log.FullName -ErrorAction SilentlyContinue }
    if (($text -match $Until).Count -gt 0 -or (Get-Date) -gt $deadline) { break }
    Start-Sleep -Seconds 20
}
$text | Where-Object { $_ -match $key } | Select-Object -Unique
"[$(Get-Date -Format HH:mm:ss)]"
