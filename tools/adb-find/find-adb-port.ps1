# Finds the rotating adb wireless-debugging port on a known Android IP.
#
# Android picks an ephemeral port for adbd every time wireless debugging is
# re-enabled, so a remembered "ip:port" goes stale constantly. mDNS is the
# intended way to discover it, but the phone only advertises while the Wireless
# debugging screen is reachable - when it is not advertising, a port sweep is
# the remaining option.
param(
    [string]$Ip = '192.168.0.178',
    [int]$From  = 30000,
    [int]$To    = 61000,
    [int]$Batch = 800,
    [int]$TimeoutMs = 300
)

$open = New-Object System.Collections.Generic.List[int]

for ($start = $From; $start -le $To; $start += $Batch) {
    $end = [Math]::Min($start + $Batch - 1, $To)
    $clients = @()
    $tasks   = @()

    foreach ($p in $start..$end) {
        $c = New-Object System.Net.Sockets.TcpClient
        $clients += $c
        $tasks   += $c.ConnectAsync($Ip, $p)
    }

    [System.Threading.Tasks.Task]::WaitAll($tasks, $TimeoutMs) | Out-Null

    for ($i = 0; $i -lt $tasks.Count; $i++) {
        if ($tasks[$i].Status -eq 'RanToCompletion' -and $clients[$i].Connected) {
            $port = $start + $i
            $open.Add($port)
            Write-Output "OPEN $Ip`:$port"
        }
        $clients[$i].Close()
    }
}

if ($open.Count -eq 0) { Write-Output "no open ports in $From-$To on $Ip" }
else { Write-Output ("candidates: " + ($open -join ', ')) }
