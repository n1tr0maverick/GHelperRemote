#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Installs GHelperRemote as a Windows service.

.DESCRIPTION
    Creates the GHelperRemote Windows service, adds a firewall rule for
    inbound TCP traffic on port 5123, and starts the service.

.PARAMETER Path
    Path to the GHelperRemote.Web.exe executable.
    Defaults to $PSScriptRoot\..\publish\GHelperRemote.Web.exe

.EXAMPLE
    .\install-service.ps1
    .\install-service.ps1 -Path "C:\GHelperRemote\GHelperRemote.Web.exe"
#>
param(
    [string]$Path
)

$ErrorActionPreference = 'Stop'

# ── Verify administrator privileges ──────────────────────────────────────────
$principal = New-Object Security.Principal.WindowsPrincipal(
    [Security.Principal.WindowsIdentity]::GetCurrent()
)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error "This script must be run as Administrator. Right-click PowerShell and select 'Run as Administrator'."
    exit 1
}

# ── Service configuration ────────────────────────────────────────────────────
$ServiceName   = 'GHelperRemote'
$DisplayName   = 'G-Helper Remote Control'
$Description   = 'Remote control service for G-Helper, providing a web-based interface to manage ASUS laptop settings.'
$FirewallPort  = 5123

# ── Resolve executable path ──────────────────────────────────────────────────
if (-not $Path) {
    $Path = Join-Path $PSScriptRoot '..\publish\GHelperRemote.Web.exe'
}
$Path = [System.IO.Path]::GetFullPath($Path)

if (-not (Test-Path $Path)) {
    Write-Error "Executable not found at: $Path`nPublish the application first or specify the path with -Path."
    exit 1
}

Write-Host "Executable: $Path" -ForegroundColor Cyan

# ── Check for existing service ───────────────────────────────────────────────
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Error "Service '$ServiceName' already exists. Run uninstall-service.ps1 first to remove it."
    exit 1
}

# ── Create the Windows service ───────────────────────────────────────────────
Write-Host "Creating service '$ServiceName'..." -ForegroundColor Yellow

New-Service `
    -Name $ServiceName `
    -BinaryPathName $Path `
    -DisplayName $DisplayName `
    -Description $Description `
    -StartupType Automatic

Write-Host "Service created successfully." -ForegroundColor Green

# ── Add firewall rule ────────────────────────────────────────────────────────
$existingRule = Get-NetFirewallRule -Name $ServiceName -ErrorAction SilentlyContinue
if ($existingRule) {
    Write-Host "Firewall rule '$ServiceName' already exists, skipping." -ForegroundColor Yellow
} else {
    Write-Host "Adding firewall rule for port $FirewallPort..." -ForegroundColor Yellow

    New-NetFirewallRule `
        -Name $ServiceName `
        -DisplayName $DisplayName `
        -Direction Inbound `
        -Protocol TCP `
        -LocalPort $FirewallPort `
        -Action Allow `
        -Profile Private

    Write-Host "Firewall rule added." -ForegroundColor Green
}

# ── Start the service ────────────────────────────────────────────────────────
Write-Host "Starting service '$ServiceName'..." -ForegroundColor Yellow
Start-Service -Name $ServiceName

# ── Output status ────────────────────────────────────────────────────────────
$svc = Get-Service -Name $ServiceName
Write-Host ""
Write-Host "============================================" -ForegroundColor Cyan
Write-Host " Service Name  : $($svc.Name)"
Write-Host " Display Name  : $($svc.DisplayName)"
Write-Host " Status        : $($svc.Status)"
Write-Host " Startup Type  : $($svc.StartType)"
Write-Host " Executable    : $Path"
Write-Host " Firewall Port : $FirewallPort (TCP, Private profile)"
Write-Host "============================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "GHelperRemote service installed and running." -ForegroundColor Green
