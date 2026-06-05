# install-windows-service.ps1
#
# Installs CircleAI.Inference.Server as a Windows service via the built-in
# sc.exe controller. Microsoft.Extensions.Hosting.WindowsServices in
# CircleAI.Inference.Server detects the service-controller invocation
# automatically — no additional flags needed.
#
# Usage (elevated PowerShell):
#   .\install-windows-service.ps1 -BinaryPath "C:\Program Files\CircleAI\CircleAI.Inference.Server.exe"
#   .\install-windows-service.ps1 -Action Uninstall
#
# Inspect:
#   Get-Service CircleAI.Inference.Server
#   Get-EventLog -LogName Application -Source "CircleAI.Inference.Server" -Newest 20

[CmdletBinding()]
param(
    [ValidateSet('Install','Uninstall','Restart','Status')]
    [string] $Action = 'Install',

    [string] $ServiceName = 'CircleAI.Inference.Server',

    [string] $DisplayName = 'CircleAI Inference Server',

    [string] $Description = 'OpenAI-compatible inference server backed by Alibaba MNN. See https://github.com/bhengubv/CircleAI/blob/master/docs/DEPLOY.md',

    [string] $BinaryPath  = "$env:ProgramFiles\CircleAI\CircleAI.Inference.Server.exe",

    [string] $RuntimeRoot = "$env:ProgramData\CircleAI\runtime",

    [string] $ModelsRoot  = "$env:ProgramData\CircleAI\models",

    [string] $Urls        = 'http://0.0.0.0:8080'
)

$ErrorActionPreference = 'Stop'

function Require-Admin {
    $id = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    $isAdmin = (New-Object System.Security.Principal.WindowsPrincipal($id)).IsInRole(
        [System.Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $isAdmin) {
        throw "This script must be run as Administrator."
    }
}

function Install-CircleAIService {
    if (-not (Test-Path $BinaryPath)) {
        throw "Binary not found: $BinaryPath. Publish the project first (dotnet publish -c Release -r win-x64 --self-contained false)."
    }

    if (-not (Test-Path $RuntimeRoot)) { New-Item -ItemType Directory -Path $RuntimeRoot -Force | Out-Null }
    if (-not (Test-Path $ModelsRoot))  { New-Item -ItemType Directory -Path $ModelsRoot  -Force | Out-Null }

    Write-Host "Installing service '$ServiceName' -> $BinaryPath"

    # sc.exe is more flexible than New-Service for environment + recovery.
    & sc.exe create $ServiceName binPath= "`"$BinaryPath`"" `
        start= auto `
        DisplayName= "$DisplayName" | Out-Null
    & sc.exe description $ServiceName "$Description" | Out-Null

    # Recovery: restart 3 times in 5-minute window.
    & sc.exe failure $ServiceName reset= 300 actions= restart/5000/restart/5000/restart/5000 | Out-Null

    # Environment block — sc.exe accepts a delimited list of KEY=VAL pairs.
    $envBlock = "ASPNETCORE_URLS=$Urls`0" + `
                "ASPNETCORE_ENVIRONMENT=Production`0" + `
                "CircleAIServer__RuntimeCacheRoot=$RuntimeRoot`0" + `
                "CircleAIServer__ModelStorageRoot=$ModelsRoot"
    # Setting environment via registry is the documented Windows-service path.
    $regKey = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
    New-ItemProperty -Path $regKey -Name 'Environment' -Value $envBlock -PropertyType MultiString -Force | Out-Null

    Write-Host "Starting service…"
    Start-Service -Name $ServiceName
    Get-Service -Name $ServiceName
}

function Uninstall-CircleAIService {
    if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
        Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
        & sc.exe delete $ServiceName | Out-Null
        Write-Host "Removed service '$ServiceName'."
    } else {
        Write-Host "Service '$ServiceName' was not installed."
    }
}

function Restart-CircleAIService {
    Restart-Service -Name $ServiceName -Force
    Get-Service -Name $ServiceName
}

function Show-Status {
    Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
}

Require-Admin
switch ($Action) {
    'Install'   { Install-CircleAIService }
    'Uninstall' { Uninstall-CircleAIService }
    'Restart'   { Restart-CircleAIService }
    'Status'    { Show-Status }
}
