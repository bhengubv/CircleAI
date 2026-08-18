# clean-install.ps1
#
# Put a phone back to genuinely-never-installed, on purpose, out loud.
#
# WHY THIS EXISTS. `adb uninstall` looks like a deploy step and behaves like a
# delete command: it takes the app's private storage with it, and that is where
# the models live. Run once for an unrelated reason on 2026-08-09 it destroyed
# about 9 GB of downloaded weights, including a 22.8 GB model part-way through
# its fetch. Nothing warned, because nothing knew it was holding anything.
#
# It also does not remove everything, which is the other half of the problem:
# the external directory under /sdcard/Android/data survives, so the "clean"
# install that follows is not clean and first-run behaviour cannot be tested.
#
# So this script does two things the raw command does not:
#
#   IT COUNTS THE COST FIRST and prints it, per model, in gigabytes. A removal
#   worth doing survives being looked at. One that is not gets stopped here.
#
#   IT REMOVES EVERYTHING, private and external, then proves the phone is clean
#   rather than assuming it.
#
# Usage:
#   .\clean-install.ps1 -Serial UTKDU19919000815                 # inventory only
#   .\clean-install.ps1 -Serial UTKDU19919000815 -Confirm        # do it
#   .\clean-install.ps1 -Serial ... -Confirm -Apk path\to.apk    # and reinstall

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $Serial,
    [string] $Package = "com.bhengubv.circleai",
    [string] $Apk,
    [switch] $Confirm
)

$ErrorActionPreference = "Stop"
$adb = Join-Path $env:LOCALAPPDATA "Android\Sdk\platform-tools\adb.exe"
if (-not (Test-Path $adb)) { throw "adb not found at $adb" }

function Adb { & $adb -s $Serial @args }

Write-Host "device  : $Serial"
Write-Host "package : $Package"
Write-Host ""

# ── what is about to be lost ─────────────────────────────────────────────────
#
# Asked of the APP, not of the shell: the package is not debuggable, so its
# private store cannot be read from outside. The bench's `ls` mode is the only
# thing that can see in, and its answer goes to logcat.
Write-Host "=== what this will destroy ==="
Adb logcat -c | Out-Null
Adb shell "am start -n $Package/crc640275883f62ced8ff.BenchActivity --ez ls true" | Out-Null
Start-Sleep -Seconds 10
$inventory = Adb logcat -d -s CircleAI.Bench:* | Select-String -Pattern "—|disk:"
if ($inventory) { $inventory | ForEach-Object { "  " + ($_ -replace '.*CircleAI\.Bench: ', '') } }
else { Write-Host "  (nothing reported — app may not be installed)" }

$ext = Adb shell "du -sh /sdcard/Android/data/$Package 2>/dev/null" 2>$null
if ($ext) { Write-Host "  external: $ext" }
Write-Host ""

if (-not $Confirm) {
    Write-Host "Inventory only. Re-run with -Confirm to remove all of the above." -ForegroundColor Yellow
    exit 0
}

# ── remove ───────────────────────────────────────────────────────────────────
Write-Host "=== removing ==="
Adb shell "am force-stop $Package" | Out-Null

# External first. Uninstall usually clears it, but "usually" is how leftovers
# survive into the next install and make a first run look like a second one.
Adb shell "rm -rf /sdcard/Android/data/$Package" 2>$null | Out-Null
foreach ($vol in (Adb shell "ls /storage/ 2>/dev/null")) {
    $v = $vol.Trim()
    if ($v -and $v -notin @("self", "emulated")) {
        Adb shell "rm -rf /storage/$v/Android/data/$Package" 2>$null | Out-Null
    }
}

$out = Adb uninstall $Package 2>&1
Write-Host "  uninstall: $out"

# ── prove it ─────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "=== verifying clean ==="
$still = Adb shell "pm list packages | grep $Package" 2>$null
$leftPriv = Adb shell "ls /data/data/$Package 2>/dev/null" 2>$null
$leftExt  = Adb shell "ls /sdcard/Android/data/$Package 2>/dev/null" 2>$null

$clean = $true
if ($still)    { Write-Host "  STILL INSTALLED: $still" -ForegroundColor Red; $clean = $false }
if ($leftPriv) { Write-Host "  private data remains" -ForegroundColor Red;    $clean = $false }
if ($leftExt)  { Write-Host "  external data remains" -ForegroundColor Red;   $clean = $false }
if ($clean)    { Write-Host "  clean — nothing of $Package remains" -ForegroundColor Green }

Adb shell "df -h /data | tail -1"

# ── reinstall ────────────────────────────────────────────────────────────────
if ($Apk) {
    if (-not (Test-Path $Apk)) { throw "APK not found: $Apk" }
    Write-Host ""
    Write-Host "=== installing $([IO.Path]::GetFileName($Apk)) ==="
    Adb install $Apk | Select-Object -Last 1
    Write-Host "The next launch is a genuine first run."
}
