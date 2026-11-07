# Waits for the phone to stop being busy (idle share of the CPU over a few seconds), reading /proc/stat only. No setting changes.
param([string]$Serial = 'UTKDU19919000815', [int]$WantIdle = 80, [int]$MostMinutes = 8)
$adb = 'C:\Program Files (x86)\Android\android-sdk\platform-tools\adb.exe'
function Sample { $f = ("$(& $adb -s $Serial shell "head -1 /proc/stat" 2>$null)".Trim() -split '\s+')[1..8] | ForEach-Object { [long]$_ }; return $f }
$deadline = (Get-Date).AddMinutes($MostMinutes)
$calm = 0
while ((Get-Date) -lt $deadline) {
    $a = Sample; Start-Sleep -Seconds 5; $b = Sample
    $d = 0..7 | ForEach-Object { $b[$_] - $a[$_] }
    $total = ($d | Measure-Object -Sum).Sum
    $idle = [int](100 * $d[3] / $total); $iow = [int](100 * $d[4] / $total); $sys = [int](100 * $d[2] / $total)
    "$(Get-Date -Format HH:mm:ss) idle $idle% iowait $iow% system $sys%"
    if ($idle -ge $WantIdle) { $calm++ } else { $calm = 0 }
    if ($calm -ge 2) { "settled"; exit 0 }
}
"never settled"
