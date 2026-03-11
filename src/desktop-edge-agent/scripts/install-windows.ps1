#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Installs or uninstalls the FCC Desktop Agent as a Windows Service.

.PARAMETER Action
    "install" (default) or "uninstall"

.PARAMETER ExePath
    Full path to FccDesktopAgent.Service.exe. Defaults to the exe in the same
    directory as this script.

.EXAMPLE
    .\install-windows.ps1
    .\install-windows.ps1 -Action uninstall
    .\install-windows.ps1 -ExePath "C:\Program Files\FccDesktopAgent\FccDesktopAgent.Service.exe"
#>
param(
    [ValidateSet("install", "uninstall")]
    [string]$Action = "install",

    [string]$ExePath = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$ServiceName    = "FccDesktopAgent"
$DisplayName    = "FCC Desktop Agent"
$Description    = "Forecourt Middleware Desktop Edge Agent — buffers and uploads fuel dispense transactions."
$StartupType    = "Automatic"

if ($ExePath -eq "") {
    $ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
    $ExePath   = Join-Path $ScriptDir "FccDesktopAgent.Service.exe"
}

function Install-Service {
    if (-not (Test-Path $ExePath)) {
        Write-Error "Executable not found: $ExePath"
        exit 1
    }

    $existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($existing) {
        Write-Host "Service '$ServiceName' already exists — stopping and removing before reinstall."
        Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
        sc.exe delete $ServiceName | Out-Null
        Start-Sleep -Seconds 2
    }

    Write-Host "Creating service '$ServiceName'..."
    New-Service `
        -Name        $ServiceName `
        -DisplayName $DisplayName `
        -Description $Description `
        -BinaryPathName "`"$ExePath`"" `
        -StartupType $StartupType | Out-Null

    Write-Host "Starting service '$ServiceName'..."
    Start-Service -Name $ServiceName

    $svc = Get-Service -Name $ServiceName
    Write-Host "Service status: $($svc.Status)"
    Write-Host "Installation complete."
}

function Uninstall-Service {
    $existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if (-not $existing) {
        Write-Warning "Service '$ServiceName' not found — nothing to remove."
        return
    }

    Write-Host "Stopping service '$ServiceName'..."
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue

    Write-Host "Removing service '$ServiceName'..."
    sc.exe delete $ServiceName | Out-Null
    Write-Host "Uninstall complete."
}

switch ($Action) {
    "install"   { Install-Service }
    "uninstall" { Uninstall-Service }
}
