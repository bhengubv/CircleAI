# One question through the app's own chat screen, timed as a person sees it: taps Send, then takes raw screen frames over
# USB as fast as they come and keeps each one that differs in the chat area, with the time since the tap. Nothing but
# taps and typing reaches the phone; no setting changes. With -Typed the question is already in the box and the keyboard up.
param([string]$Text = '', [switch]$Typed, [int]$Seconds = 12, [string]$Tag = 'q1', [int]$SendX = 926, [int]$SendY = 1438)
$ErrorActionPreference = 'Continue'
$adb = 'C:\Program Files (x86)\Android\android-sdk\platform-tools\adb.exe'
$usb = 'UTKDU19919000815'
$scratch = '.'
$out = "$scratch\appframes\$Tag"
New-Item -ItemType Directory -Force -Path $out | Out-Null
$width = 1080; $height = 2312; $stride = $width * 4

function Frame {
    $psi = [System.Diagnostics.ProcessStartInfo]::new($adb, "-s $usb exec-out screencap")
    $psi.RedirectStandardOutput = $true; $psi.UseShellExecute = $false
    $p = [System.Diagnostics.Process]::Start($psi)
    $ms = [System.IO.MemoryStream]::new(10100000)
    $p.StandardOutput.BaseStream.CopyTo($ms)
    $p.WaitForExit()
    , $ms.ToArray()
}

if (-not $Typed) {
    & $adb -s $usb shell "input tap 348 1862"
    Start-Sleep -Milliseconds 1200
    & $adb -s $usb shell "input text '$($Text.Replace(' ', '%s').Replace("'", ''))'"
    Start-Sleep -Milliseconds 700
}

$sha = [System.Security.Cryptography.SHA1]::Create()
$t0Phone = "$(& $adb -s $usb shell "date '+%m-%d %H:%M:%S.000'" 2>$null)".Trim()
$clock = [System.Diagnostics.Stopwatch]::StartNew()
& $adb -s $usb shell "input tap $SendX $SendY"
$tapped = $clock.Elapsed.TotalSeconds
"tap returned at $($tapped.ToString('F2')) s (phone clock $t0Phone)"

$last = ''
$n = 0
while ($clock.Elapsed.TotalSeconds -lt $Seconds) {
    $bytes = Frame
    $at = $clock.Elapsed.TotalSeconds
    $header = $bytes.Length - $stride * $height
    if ($header -lt 0) { continue }
    $hash = [Convert]::ToHexString($sha.ComputeHash($bytes, $header + 150 * $stride, 1900 * $stride))
    if ($hash -ne $last) {
        $last = $hash
        $n++
        [System.IO.File]::WriteAllBytes("$out\f$('{0:D2}' -f $n)-$($at.ToString('F2')).raw", $bytes[$header..($bytes.Length - 1)])
        "frame $n changed by $($at.ToString('F2')) s"
    }
}

Get-ChildItem $out -Filter *.raw | ForEach-Object {
    & ffmpeg -y -loglevel error -f rawvideo -pixel_format rgba -video_size 1080x2312 -i $_.FullName -vf scale=360:-1 ($_.FullName -replace '\.raw$', '.png')
    Remove-Item $_.FullName
}
$lines = @(& $adb -s $usb logcat -d -v threadtime -T "$t0Phone" 2>$null)
$lines | Where-Object { $_ -cmatch 'CircleAI\.(Engine|Asks|Context|Soak|Link|Chat|Turn)\b|CIRCLEAI-TURN' -and $_ -match 'prompt tokens|CIRCLEAI-TURN|accepted|the turn held|Succeeded|Failed' } |
    ForEach-Object { $_.Substring(0, [Math]::Min(250, $_.Length)) }
"frames in $out"
