#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Uninstalls the GHelperRemote Windows service.

.DESCRIPTION
    Stops the GHelperRemote service if running, removes the Windows service,
    and deletes the associated firewall rule.

.EXAMPLE
    .\uninstall-service.ps1
#>

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
$ServiceName = 'GHelperRemote'

# ── Stop the service if running ──────────────────────────────────────────────
$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if (-not $svc) {
    Write-Host "Service '$ServiceName' not found. Nothing to uninstall." -ForegroundColor Yellow
} else {
    if ($svc.Status -eq 'Running') {
        Write-Host "Stopping service '$ServiceName'..." -ForegroundColor Yellow
        Stop-Service -Name $ServiceName -Force
        # Wait for the service to fully stop
        $svc.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
        Write-Host "Service stopped." -ForegroundColor Green
    } else {
        Write-Host "Service '$ServiceName' is not running (status: $($svc.Status))." -ForegroundColor Yellow
    }

    # ── Remove the service ───────────────────────────────────────────────────
    Write-Host "Removing service '$ServiceName'..." -ForegroundColor Yellow
    $scResult = sc.exe delete $ServiceName
    if ($LASTEXITCODE -eq 0) {
        Write-Host "Service removed successfully." -ForegroundColor Green
    } else {
        Write-Error "Failed to remove service. sc.exe output: $scResult"
        exit 1
    }
}

# ── Remove the firewall rule ────────────────────────────────────────────────
$rule = Get-NetFirewallRule -Name $ServiceName -ErrorAction SilentlyContinue
if ($rule) {
    Write-Host "Removing firewall rule '$ServiceName'..." -ForegroundColor Yellow
    Remove-NetFirewallRule -Name $ServiceName
    Write-Host "Firewall rule removed." -ForegroundColor Green
} else {
    Write-Host "Firewall rule '$ServiceName' not found, skipping." -ForegroundColor Yellow
}

# ── Output status ────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "============================================" -ForegroundColor Cyan
Write-Host " GHelperRemote service uninstalled."
Write-Host "============================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Uninstall complete." -ForegroundColor Green
