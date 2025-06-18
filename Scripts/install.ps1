param (
    [string]$ExePath = "$(Split-Path -Parent $MyInvocation.MyCommand.Definition)\ProcessMonitorService.exe",
    [string]$ServiceName = "ProcessMonitorService"
)

#Requires -RunAsAdministrator

# PowerShell Version pruefen
if ($PSVersionTable.PSVersion.Major -lt 5) {
    Write-Error "Dieses Skript erfordert mindestens PowerShell Version 5.0"
    exit 1
}

# Funktion fuer sicheren Service-Stop mit Timeout
function Stop-ServiceSafely {
    param (
        [string]$Name,
        [int]$TimeoutSeconds = 30
    )
    
    try {
        $service = Get-Service -Name $Name -ErrorAction SilentlyContinue
        if (-not $service -or $service.Status -ne 'Running') {
            return $true
        }
        
        Write-Host "Stoppe Service '$Name'..." -ForegroundColor Yellow
        Stop-Service -Name $Name -Force -ErrorAction Stop
        
        # Warten bis Service vollstaendig gestoppt ist
        $timer = 0
        do {
            Start-Sleep -Seconds 1
            $timer++
            $service = Get-Service -Name $Name -ErrorAction SilentlyContinue
        } while ($service.Status -ne 'Stopped' -and $timer -lt $TimeoutSeconds)
        
        if ($service.Status -ne 'Stopped') {
            throw "Service konnte nicht innerhalb von $TimeoutSeconds Sekunden gestoppt werden."
        }
        
        Write-Host "Service gestoppt" -ForegroundColor Green
        return $true
        
    }
    catch {
        Write-Host "Fehler beim Stoppen des Services: $($_.Exception.Message)" -ForegroundColor Red
        return $false
    }
}

# Funktion fuer automatischen Administrator-Neustart
function Start-AsAdministrator {
    try {
        Write-Host "Starte PowerShell mit Administrator-Rechten neu..." -ForegroundColor Yellow
        
        # Parameter fuer Neustart zusammenstellen
        $arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$($MyInvocation.MyCommand.Path)`""
        
        # Urspruengliche Parameter hinzufuegen
        if ($PSBoundParameters.Count -gt 0) {
            foreach ($param in $PSBoundParameters.GetEnumerator()) {
                $arguments += " -$($param.Key) `"$($param.Value)`""
            }
        }
        
        Start-Process -FilePath "powershell.exe" -Verb RunAs -ArgumentList $arguments
        exit 0
        
    }
    catch {
        Write-Host "Fehler beim Neustart mit Administrator-Rechten: $($_.Exception.Message)" -ForegroundColor Red
        return $false
    }
}

# Hauptlogik
try {
    Write-Host "=== Service Installation ===" -ForegroundColor Cyan
    Write-Host "Service: $ServiceName" -ForegroundColor White
    Write-Host "Executable: $ExePath" -ForegroundColor White
    Write-Host "PowerShell Version: $($PSVersionTable.PSVersion)" -ForegroundColor Gray
    Write-Host ""
    
    # Administrator-Rechte pruefen (redundant wegen #Requires, aber fuer bessere Fehlermeldung)
    if (-not ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        Write-Host "Administrator-Rechte erforderlich!" -ForegroundColor Red
        Write-Host ""
        Write-Host "Loesungsoptionen:" -ForegroundColor Yellow
        Write-Host "- PowerShell als Administrator oeffnen" -ForegroundColor White
        Write-Host "- Rechtsklick auf PowerShell -> 'Als Administrator ausfuehren'" -ForegroundColor White
        Write-Host ""
        
        $restart = Read-Host "Automatisch mit Administrator-Rechten neu starten? (j/n)"
        if ($restart -eq 'j' -or $restart -eq 'J' -or $restart -eq 'y' -or $restart -eq 'Y') {
            Start-AsAdministrator
        }
        
        throw "Installation abgebrochen - Administrator-Rechte erforderlich."
    }
    
    Write-Host "Administrator-Rechte bestaetigt" -ForegroundColor Green
    Write-Host ""
    
    # Service-Datei ueberpruefen
    Write-Host "Ueberpruefe Service-Datei..." -ForegroundColor Yellow
    
    if (-not (Test-Path $ExePath)) {
        throw "Service-Executable nicht gefunden: $ExePath"
    }
    
    # Zusaetzliche Validierung der Datei
    $fileInfo = Get-Item $ExePath
    if ($fileInfo.Length -eq 0) {
        throw "Service-Executable ist leer: $ExePath"
    }
    
    Write-Host "Service-Datei gefunden ($([math]::Round($fileInfo.Length/1KB, 2)) KB)" -ForegroundColor Green
    Write-Host ""
    
    # Existierenden Service behandeln
    Write-Host "Ueberpruefe existierenden Service..." -ForegroundColor Yellow
    
    $existingService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    
    if ($existingService) {
        Write-Host "Service '$ServiceName' existiert bereits." -ForegroundColor Yellow
        
        # Service sicher stoppen
        if (-not (Stop-ServiceSafely -Name $ServiceName)) {
            throw "Konnte existierenden Service nicht stoppen."
        }
        
        # Service loeschen
        Write-Host "Loesche existierenden Service..." -ForegroundColor Yellow
        $result = & sc.exe delete $ServiceName 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "Fehler beim Loeschen des existierenden Services: $result"
        }
        Write-Host "Existierender Service geloescht" -ForegroundColor Green
        
        # Warten bis Windows den Service vollstaendig entfernt hat
        Start-Sleep -Seconds 3
    }
    else {
        Write-Host "Kein existierender Service gefunden" -ForegroundColor Green
    }
    
    Write-Host ""
    
    # Neuen Service erstellen
    Write-Host "Erstelle Service '$ServiceName'..." -ForegroundColor Yellow
    
    $createResult = & sc.exe create $ServiceName binPath= "`"$ExePath`"" start=auto DisplayName="Process Monitor Service" 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Fehler beim Erstellen des Services: $createResult"
    }
    Write-Host "Service erstellt" -ForegroundColor Green
    
    # Service-Beschreibung setzen
    Write-Host "Setze Service-Beschreibung..." -ForegroundColor Yellow
    & sc.exe description $ServiceName "Ueberwacht Prozess-Start und -Stop Ereignisse fuer konfigurierte Anwendungen" | Out-Null
    if ($LASTEXITCODE -eq 0) {
        Write-Host "Service-Beschreibung gesetzt" -ForegroundColor Green
    }
    else {
        Write-Host "Warnung: Service-Beschreibung konnte nicht gesetzt werden" -ForegroundColor Yellow
    }
    
    # Service starten
    Write-Host "Starte Service '$ServiceName'..." -ForegroundColor Yellow
    
    Start-Service -Name $ServiceName -ErrorAction Stop
    
    # Service-Status final ueberpruefen
    Start-Sleep -Seconds 2
    $service = Get-Service -Name $ServiceName
    
    Write-Host ""
    if ($service.Status -eq 'Running') {
        Write-Host "=== INSTALLATION ERFOLGREICH ===" -ForegroundColor Green
    }
    else {
        Write-Host "=== INSTALLATION MIT WARNUNG ===" -ForegroundColor Yellow
        Write-Host "Service erstellt, aber nicht gestartet (Status: $($service.Status))" -ForegroundColor Yellow
    }
    
    Write-Host ""
    Write-Host "Service-Details:" -ForegroundColor Cyan
    Write-Host "- Name: $ServiceName" -ForegroundColor White
    Write-Host "- Status: $($service.Status)" -ForegroundColor White
    Write-Host "- Starttyp: Automatisch" -ForegroundColor White
    Write-Host "- Executable: $ExePath" -ForegroundColor White
    Write-Host ""
    Write-Host "Service-Management Befehle:" -ForegroundColor Cyan
    Write-Host "- Status pruefen: Get-Service -Name $ServiceName" -ForegroundColor White
    Write-Host "- Service stoppen: Stop-Service -Name $ServiceName" -ForegroundColor White
    Write-Host "- Service starten: Start-Service -Name $ServiceName" -ForegroundColor White
    Write-Host "- Service deinstallieren: sc.exe delete $ServiceName" -ForegroundColor White
    Write-Host ""
    
}
catch {
    Write-Host ""
    Write-Host "INSTALLATION FEHLGESCHLAGEN" -ForegroundColor Red
    Write-Host "Fehler: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host ""
    Write-Host "Moegliche Loesungen:" -ForegroundColor Yellow
    Write-Host "- Ueberpruefen Sie, ob die Datei '$ExePath' existiert und ausfuehrbar ist" -ForegroundColor White
    Write-Host "- Stellen Sie sicher, dass kein anderer Service den Namen '$ServiceName' verwendet" -ForegroundColor White
    Write-Host "- Ueberpruefen Sie die Windows Event Logs (Anwendung/System)" -ForegroundColor White
    Write-Host "- Fuehren Sie 'services.msc' aus, um den Service-Status zu ueberpruefen" -ForegroundColor White
    Write-Host ""
    
    Read-Host "Druecken Sie Enter zum Beenden"
    exit 1
}

# Set-SecureACLs.ps1 ausfuehren (falls vorhanden)
$aclScript = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Definition) "Set-SecureACLs.ps1"
if (Test-Path $aclScript) {
    Write-Host "Fuehre Sicherheits-Konfiguration aus..." -ForegroundColor Yellow
    try {
        & $aclScript
        Write-Host "Sicherheits-Konfiguration abgeschlossen" -ForegroundColor Green
    }
    catch {
        Write-Host "Warnung: Sicherheits-Konfiguration fehlgeschlagen: $($_.Exception.Message)" -ForegroundColor Yellow
    }
}
else {
    Write-Host "Set-SecureACLs.ps1 nicht gefunden - ueberspringe Sicherheits-Konfiguration" -ForegroundColor Gray
}

Write-Host ""
Write-Host "Installation abgeschlossen." -ForegroundColor Green