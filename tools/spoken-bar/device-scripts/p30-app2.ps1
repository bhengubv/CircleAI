# One question through the app's own chat screen. Taps the box, types, taps Send, then takes screen frames over USB until
# the time is up (none with -NoFrames, so the capture cannot slow the brain), and prints the service's own timing for the
# turn. The tap time comes from the phone's clock, in the same shell as the tap. Taps and typing only; no setting changes.
param([string]$Text = '', [int]$Seconds = 14, [string]$Tag = 'q', [int]$BoxX = 250, [int]$BoxY = 1690, [int]$SendX = 926, [int]$SendY = 1438, [switch]$NoFrames, [switch]$StopAfterTyping, [switch]$Typed, [switch]$Scroll)
$ErrorActionPreference = 'Continue'
$adb = 'C:\Program Files (x86)\Android\android-sdk\platform-tools\adb.exe'
$usb = 'UTKDU19919000815'
$scratch = '.'
$out = "$scratch\appframes\$Tag"
New-Item -ItemType Directory -Force -Path $out | Out-Null

function Png([string]$path) {
    $psi = [System.Diagnostics.ProcessStartInfo]::new($adb, "-s $usb exec-out screencap -p")
    $psi.RedirectStandardOutput = $true; $psi.UseShellExecute = $false
    $p = [System.Diagnostics.Process]::Start($psi)
    $f = [System.IO.File]::Create($path)
    $p.StandardOutput.BaseStream.CopyTo($f)
    $f.Close()
    $p.WaitForExit()
}

if (-not $Typed) {
& $adb -s $usb shell "input tap $BoxX $BoxY"
Start-Sleep -Milliseconds 1200
& $adb -s $usb shell "input text '$($Text.Replace(' ', '%s').Replace("'", ''))'"
Start-Sleep -Milliseconds 900
}
if ($StopAfterTyping) { Png "$out\typed.png"; "typed; frame at $out\typed.png"; exit 0 }

$since = "$(& $adb -s $usb shell "date '+%m-%d %H:%M:%S.000'" 2>$null)".Trim()
$stamps = "$(& $adb -s $usb shell "date +%s.%N; input tap $SendX $SendY; date +%s.%N" 2>$null)" -split '\s+' | Where-Object { $_ }
$clock = [System.Diagnostics.Stopwatch]::StartNew()
$began = [double]::Parse($stamps[0], [Globalization.CultureInfo]::InvariantCulture)
$tapped = [double]::Parse($stamps[1], [Globalization.CultureInfo]::InvariantCulture)
"tap took $(($tapped - $began).ToString('F2', [Globalization.CultureInfo]::InvariantCulture)) s on the phone; epoch $($stamps[1])"

if ($Scroll) { Start-Sleep -Milliseconds 700; & $adb -s $usb shell "input swipe 540 1300 540 300 250" }

$n = 0
if (-not $NoFrames) {
    while ($clock.Elapsed.TotalSeconds -lt $Seconds) {
        $from = $clock.Elapsed.TotalSeconds
        $n++
        $name = "f$('{0:D2}' -f $n).png"
        Png "$out\$name"
        "$name taken between $($from.ToString('F2', [Globalization.CultureInfo]::InvariantCulture)) and $($clock.Elapsed.TotalSeconds.ToString('F2', [Globalization.CultureInfo]::InvariantCulture)) s after the tap"
    }
} else {
    Start-Sleep -Seconds $Seconds
}

$lines = @(& $adb -s $usb logcat -d -v threadtime -T "$since" 2>$null)
$lines | Where-Object { $_ -cmatch 'CircleAI\.(Engine|Asks|Context|Soak|Link|Chat|Turn)\b|CIRCLEAI-TURN' -and $_ -match 'prompt tokens|CIRCLEAI-TURN|accepted|the turn held|Succeeded|Failed' } |
    ForEach-Object { $_.Substring(0, [Math]::Min(250, $_.Length)) }
"frames: $n in $out"
