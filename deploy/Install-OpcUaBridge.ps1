<#
.SYNOPSIS
    Installs OPC UA Bridge as a Windows service.

.DESCRIPTION
    Copies the published files to the install directory, registers the service, configures
    it to restart after a crash, and opens the firewall for the mirrored OPC UA endpoint.

    Re-running this upgrades in place: config\, pki\ and Logs\ are left alone, so the
    captured namespace and the certificates the upstream server trusts survive.

.EXAMPLE
    .\Install-OpcUaBridge.ps1
    .\Install-OpcUaBridge.ps1 -InstallPath D:\OpcUaBridge -UaPort 4855
#>
[CmdletBinding()]
param(
    # Not Program Files by default: the service writes Logs, config and pki beside its own
    # executable, and Program Files ACLs make that a fight with no upside.
    [string]$InstallPath = 'C:\OpcUaBridge',
    [string]$ServiceName = 'OpcUaBridge',
    [int]$UaPort = 4841,
    [int]$WebPort = 5080,
    [string]$SourcePath = $PSScriptRoot
)

$ErrorActionPreference = 'Stop'

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
        ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this from an elevated PowerShell prompt.'
}

# The published output is the parent of deploy\, unless -SourcePath says otherwise.
if ($SourcePath -like '*\deploy') { $SourcePath = Split-Path $SourcePath -Parent }

if (-not (Test-Path (Join-Path $SourcePath 'OpcUaBridge.exe'))) {
    throw "OpcUaBridge.exe was not found in '$SourcePath'. Point -SourcePath at the published folder."
}

if (Get-Service $ServiceName -ErrorAction SilentlyContinue) {
    Write-Host "Stopping the existing $ServiceName service."
    Stop-Service $ServiceName -Force -ErrorAction SilentlyContinue
    & sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
}

Write-Host "Copying to $InstallPath."
New-Item -ItemType Directory -Path $InstallPath -Force | Out-Null

# Operator state is preserved across upgrades.
Get-ChildItem -Path $SourcePath -Exclude 'config', 'pki', 'Logs' |
    Copy-Item -Destination $InstallPath -Recurse -Force

$exe = Join-Path $InstallPath 'OpcUaBridge.exe'

Write-Host "Registering the $ServiceName service."
New-Service -Name $ServiceName `
            -BinaryPathName "`"$exe`"" `
            -DisplayName 'OPC UA Bridge' `
            -StartupType Automatic `
            -Description 'OPC UA client-to-server gateway: keeps a resilient connection to an upstream OPC UA server and mirrors its address space.' | Out-Null

# Restart after a crash. Not optional for a gateway something else depends on.
& sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/10000/restart/30000 | Out-Null

if (-not (Get-NetFirewallRule -DisplayName "OPC UA Bridge (opc.tcp $UaPort)" -ErrorAction SilentlyContinue)) {
    Write-Host "Opening TCP $UaPort for the mirrored endpoint."
    New-NetFirewallRule -DisplayName "OPC UA Bridge (opc.tcp $UaPort)" `
                        -Direction Inbound -Action Allow -Protocol TCP -LocalPort $UaPort | Out-Null
}

Write-Host 'Starting the service.'
Start-Service $ServiceName

Write-Host @"

OPC UA Bridge is installed.

  Dashboard          http://127.0.0.1:$WebPort   (loopback only; browse from this machine)
  Mirrored endpoint  opc.tcp://$($env:COMPUTERNAME):$UaPort/OpcUaBridge
  Logs               $InstallPath\Logs
  Configuration      $InstallPath\appsettings.json

Next steps:
  1. Set Bridge:Upstream:EndpointUrl in appsettings.json, then restart the service.
  2. To reuse the certificates OPC UA Exporter already had working:
       & '$exe' --import-exporter-certificates
  3. Capture the namespace from the dashboard. Until you do, the mirrored endpoint
     serves no tags.
"@
