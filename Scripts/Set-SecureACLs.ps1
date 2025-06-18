#Requires -RunAsAdministrator

# PowerShell Version prüfen (falls gewünscht)
if ($PSVersionTable.PSVersion.Major -lt 5) {
    Write-Error "Dieses Skript erfordert mindestens PowerShell Version 5.0"
    exit 1
}

Write-Host "Administrator-Rechte bestaetigt" -ForegroundColor Green
Write-Host ""

Write-Host "Erstelle Ordner C:\ProgramData\ProcessMonitorService ..." -ForegroundColor Yellow
New-Item -Path "C:\ProgramData\ProcessMonitorService" -ItemType Directory -Force | Out-Null
Write-Host "Ordner C:\ProgramData\ProcessMonitorService erstellt" -ForegroundColor Green
Write-Host ""
Write-Host "Setze ACLs auf C:\ProgramData\ProcessMonitorService ..." -ForegroundColor Yellow

# ACL-Objekt erstellen und konfigurieren
$acl = Get-Acl "C:\ProgramData\ProcessMonitorService"
$acl.SetAccessRuleProtection($true, $false)  # Vererbung entfernen

# Administratoren-Gruppe
$adminSid = [System.Security.Principal.SecurityIdentifier]"S-1-5-32-544"
$adminRule = New-Object System.Security.AccessControl.FileSystemAccessRule($adminSid, "FullControl", "ContainerInherit,ObjectInherit", "None", "Allow")
$acl.SetAccessRule($adminRule)

# SYSTEM
$systemSid = [System.Security.Principal.SecurityIdentifier]"S-1-5-18"
$systemRule = New-Object System.Security.AccessControl.FileSystemAccessRule($systemSid, "FullControl", "ContainerInherit,ObjectInherit", "None", "Allow")
$acl.SetAccessRule($systemRule)

# ACL anwenden
Set-Acl "C:\ProgramData\ProcessMonitorService" $acl

Write-Host "ACLs auf C:\ProgramData\ProcessMonitorService gesetzt" -ForegroundColor Green