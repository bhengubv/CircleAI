# install-windows-service.ps1
#
# Registers CircleAIService with the Service Control Manager, which is what was missing:
# the EXE was a console application called "Service" - PE subsystem 3, WINDOWS_CUI - that
# printed "Ctrl-C to stop." and waited. Nothing ever answered the controller, so it never
# appeared in the Services listings, never started at boot, never restarted after a crash,
# and died with the console that launched it.
#
# The binary needs no flag: Program.Main asks WindowsServiceHelpers.IsWindowsService(),
# which reads whether the controller started this process, so one EXE serves both a
# console run and a service run and the two cannot disagree about which it is.
#
# Usage (elevated PowerShell):
#   .\install-windows-service.ps1 -BinaryPath "C:\Program Files\CircleAI\CircleAIService-Windows.exe"
#   .\install-windows-service.ps1 -Action Status
#   .\install-windows-service.ps1 -Action Uninstall
#
# Inspect:
#   Get-Service CircleAIService
#   Get-Content "$env:ProgramData\CircleAI\service\service.log" -Tail 40
#   Get-WinEvent -ProviderName CircleAIService -MaxEvents 20
#
# The address is NOT printed anywhere a service can print to, so a client reads it from
#   $env:ProgramData\CircleAI\service\endpoint.json      { port, token, pid, startedUtc }
# which the service writes on every start and deletes on a clean stop. It is locked to
# SYSTEM and Administrators full, Users read.

[CmdletBinding()]
param(
    [ValidateSet('Install','Uninstall','Restart','Status')]
    [string] $Action = 'Install',

    [string] $ServiceName = 'CircleAIService',

    [string] $DisplayName = 'Circle AI Service',

    [string] $Description = 'Owns the local models and answers over loopback for every app on this machine. https://github.com/bhengubv/CircleAI',

    [string] $BinaryPath  = "$env:ProgramFiles\CircleAI\CircleAIService-Windows.exe",

    # MACHINE-WIDE, NOT PER-USER, AND THIS IS THE PART THAT BITES. Under LocalSystem
    # LocalApplicationData resolves to C:\Windows\System32\config\systemprofile\AppData\
    # Local - writable, real, and a completely different store from the one a console run
    # filled. Left to default, an installed service would report "no model here yet" on a
    # machine holding every model it needs and start re-downloading gigabytes. The binary
    # already defaults to ProgramData when the controller starts it; these are set anyway
    # so the location is written down where somebody can read it.
    [string] $ModelsRoot = "$env:ProgramData\CircleAI\models",

    [string] $DataRoot   = "$env:ProgramData\CircleAI\service",

    # The account. LocalSystem can write ProgramData and needs no password, which is why
    # it is the default - a service account with a password is a credential this script
    # would have to handle, and it will not.
    [string] $Account = 'LocalSystem'
)

$ErrorActionPreference = 'Stop'

function Require-Admin {
    $id = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    $isAdmin = (New-Object System.Security.Principal.WindowsPrincipal($id)).IsInRole(
        [System.Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $isAdmin) { throw 'This script must be run as Administrator.' }
}

function Install-Service {
    if (-not (Test-Path $BinaryPath)) {
        throw "Binary not found: $BinaryPath. Publish it first, e.g. " +
              "dotnet publish src\CircleAIService\CircleAIService.csproj -c Release -f net10.0 -r win-x64 --self-contained"
    }

    # A SELF-EXTRACTING SINGLE FILE NEEDS A WRITABLE EXTRACTION DIRECTORY, and under
    # LocalSystem that is the system profile's temp. It is writable, so nothing to do -
    # but the FIRST start pays the unpack, which is why the service's start timeout
    # matters below and why a first `Start-Service` can take longer than a second one.
    foreach ($dir in @($ModelsRoot, $DataRoot)) {
        if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    }

    if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
        Write-Host "Service '$ServiceName' already exists - stopping and replacing its configuration."
        Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
        & sc.exe delete $ServiceName | Out-Null
        Start-Sleep -Milliseconds 500
    }

    Write-Host "Installing '$ServiceName' -> $BinaryPath"

    # sc.exe rather than New-Service: the recovery actions and the environment block below
    # have no New-Service equivalent. Note sc.exe's argument form - `key= value`, with the
    # space AFTER the equals sign - which is not a typo and is not optional.
    & sc.exe create $ServiceName binPath= "`"$BinaryPath`"" start= auto obj= $Account DisplayName= "$DisplayName" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "sc.exe create failed with exit $LASTEXITCODE" }

    & sc.exe description $ServiceName "$Description" | Out-Null

    # RESTART AFTER A CRASH, which a console application could never do. Three restarts
    # at 5 s, counter reset after an hour of staying up.
    & sc.exe failure $ServiceName reset= 3600 actions= restart/5000/restart/5000/restart/5000 | Out-Null

    # Brains are gigabytes and the first start may unpack a self-contained EXE, so the
    # controller is told to wait rather than killing it as hung. The endpoint binds before
    # any of the model work now, so this is headroom for the unpack, not for the models.
    & sc.exe config $ServiceName start= delayed-auto | Out-Null

    # The environment, by the documented Windows-service route: a MultiString under the
    # service's own registry key. There is no sc.exe verb for it.
    $regKey = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
    New-ItemProperty -Path $regKey -Name 'Environment' -PropertyType MultiString -Force -Value @(
        "CIRCLEAI_MODELS=$ModelsRoot",
        "CIRCLEAI_DATA=$DataRoot"
    ) | Out-Null

    Write-Host "Starting '$ServiceName'..."
    Start-Service -Name $ServiceName
    Get-Service -Name $ServiceName | Format-List Name, DisplayName, Status, StartType

    Write-Host ''
    Write-Host "models:   $ModelsRoot"
    Write-Host "data:     $DataRoot"
    Write-Host "log:      $(Join-Path $DataRoot 'service.log')"
    Write-Host "address:  $(Join-Path $DataRoot 'endpoint.json')   (port + token; SYSTEM/Administrators full, Users read)"
}

function Uninstall-Service {
    if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
        Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
        & sc.exe delete $ServiceName | Out-Null
        Write-Host "Removed '$ServiceName'."
        # THE MODELS STAY. They are gigabytes somebody paid for on a metered link, and an
        # uninstall that takes them is the Android bundle-format mistake this repo already
        # records costing 2 GB on a P30. Remove them by hand if that is what is wanted.
        Write-Host "Left in place: $ModelsRoot (gigabytes - delete it yourself if you mean to)."
    }
    else { Write-Host "Service '$ServiceName' was not installed." }
}

Require-Admin
switch ($Action) {
    'Install'   { Install-Service }
    'Uninstall' { Uninstall-Service }
    'Restart'   { Restart-Service -Name $ServiceName -Force; Get-Service -Name $ServiceName }
    'Status'    { Get-Service -Name $ServiceName -ErrorAction SilentlyContinue | Format-List Name, DisplayName, Status, StartType }
}
