<#
.SYNOPSIS
    Removes the OPC UA Bridge Windows service.

.DESCRIPTION
    Stops and deregisters the service and removes its firewall rule. The install directory
    is left in place by default, so the captured namespace, the certificates and the logs
    survive -- pass -RemoveFiles to delete them too.
#>
[CmdletBinding()]
param(
    [string]$InstallPath = 'C:\OpcUaBridge',
    [string]$ServiceName = 'OpcUaBridge',
    [int]$UaPort = 4841,
    [switch]$RemoveFiles
)

$ErrorActionPreference = 'Stop'

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
        ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this from an elevated PowerShell prompt.'
}

if (Get-Service $ServiceName -ErrorAction SilentlyContinue) {
    Write-Host "Stopping and removing the $ServiceName service."
    Stop-Service $ServiceName -Force -ErrorAction SilentlyContinue
    & sc.exe delete $ServiceName | Out-Null
} else {
    Write-Host "No $ServiceName service is installed."
}

$rule = Get-NetFirewallRule -DisplayName "OPC UA Bridge (opc.tcp $UaPort)" -ErrorAction SilentlyContinue
if ($rule) {
    Write-Host 'Removing the firewall rule.'
    $rule | Remove-NetFirewallRule
}

if ($RemoveFiles -and (Test-Path $InstallPath)) {
    Write-Warning "Deleting $InstallPath, including the captured namespace and the certificate stores."
    Remove-Item $InstallPath -Recurse -Force
} elseif (Test-Path $InstallPath) {
    Write-Host "Left $InstallPath in place. Pass -RemoveFiles to delete it."
}
