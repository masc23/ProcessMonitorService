#Requires -Version 5.1

<#
.SYNOPSIS
    Verarbeitet JSON-Log-Dateien des ProcessMonitorService

.DESCRIPTION
    Dieses Skript liest die strukturierten JSON-Log-Dateien des ProcessMonitorService
    und ermöglicht das Filtern und Analysieren von Prozess-Events.

.PARAMETER LogPath
    Pfad zu den JSON-Log-Dateien (Standard: C:\ProgramData\ProcessMonitorService\logs\service-*.json)

.PARAMETER ProcessName
    Filtert nach spezifischem Prozessnamen (z.B. "chrome.exe")

.PARAMETER EventType
    Filtert nach Event-Typ: "Start", "Stop" oder "All" (Standard: All)

.PARAMETER Days
    Zeigt Events der letzten X Tage an (Standard: 1)

.PARAMETER Export
    Exportiert gefilterte Ergebnisse in CSV-Datei

.PARAMETER ForPipeline
    Ermöglicht Weiterverarbeitung der Ergebnisse über eine Pipeline

.PARAMETER OnlyProcessRuntimes
    Zeigt nur Start- und Stop-Zeiten von Prozessen an, berechnet die Laufzeit

.EXAMPLE
    .\process-JSONFiles.ps1
    Zeigt alle Events des aktuellen Tages

.EXAMPLE
    .\process-JSONFiles.ps1 -ProcessName "chrome.exe" -EventType "Start"
    Zeigt nur Chrome-Start-Events

.EXAMPLE
    .\process-JSONFiles.ps1 -Days 7 -Export
    Exportiert alle Events der letzten 7 Tage in CSV
#>

param(
    [string]$LogPath = "C:\ProgramData\ProcessMonitorService\logs\service-*.json",
    [string]$ProcessName = "",
    [ValidateSet("Start", "Stop", "All")]
    [string]$EventType = "All",
    [int]$Days = 1,
    [switch]$Export,
    [switch]$ForPipeline,
    [switch]$OnlyProcessRuntimes
)

function Write-ColorOutput {
    param([string]$Message, [string]$Color = "White")
    Write-Host $Message -ForegroundColor $Color
}

function Get-ProcessEvents {
    param([string[]]$FilePaths)
    
    $allEvents = @()
    
    foreach ($file in $FilePaths) {
        try {
            Write-ColorOutput "Verarbeite: $($file.Name)" -Color "Gray"
            
            $content = Get-Content $file -Encoding UTF8
            $myEvents = $content | ForEach-Object {
                try {
                    $_ | ConvertFrom-Json
                }
                catch {
                    Write-Warning "Fehlerhafte JSON-Zeile in $($file.Name): $_"
                }
            }
            
            $allEvents += $myEvents
        }
        catch {
            Write-Warning "Fehler beim Lesen von $($file.Name): $($_.Exception.Message)"
        }
    }
    
    return $allEvents
}

function Convert-ToGermanTime {
    param($UtcTimeString)
    if ($UtcTimeString) {
        $sourceTime = ([DateTime]$UtcTimeString)
        $germanTimeZone = [TimeZoneInfo]::FindSystemTimeZoneById("W. Europe Standard Time")
        $convertedTime = [TimeZoneInfo]::ConvertTime($sourceTime, $germanTimeZone)
        return $convertedTime.ToString("yyyy-MM-dd HH:mm:ss")
    }
    return "N/A"
}

function Format-ProcessEvent {
    param($myEvent)

    $timestamp = Convert-ToGermanTime -UtcTimeString $myEvent.'@t'
    
    [PSCustomObject]@{
        Timestamp      = $timestamp
        EventType      = $myEvent.EventType ?? "N/A"
        ProcessName    = $myEvent.ProcessName ?? "N/A"
        ProcessId      = $myEvent.ProcessId ?? "N/A"
        UserSid        = $myEvent.UserSid ?? "N/A"
        ExecutablePath = $myEvent.ExecutablePath ?? "N/A"
        CommandLine    = $myEvent.CommandLine ?? "N/A"
    }
}

# Hauptlogik
try {
    Write-ColorOutput "=== ProcessMonitorService Log-Analyse ===" -Color "Cyan"
    Write-ColorOutput "Suchpfad: $LogPath" -Color "Gray"
    
    # Prüfe ob Dateien existieren
    $logFiles = Get-ChildItem -Path $LogPath -ErrorAction SilentlyContinue
    
    if (-not $logFiles) {
        Write-ColorOutput "❌ Keine Log-Dateien gefunden unter: $LogPath" -Color "Red"
        exit 1
    }
    
    # Filtere nach Datum falls gewünscht
    if ($Days -gt 0) {
        $cutoffDate = (Get-Date).AddDays(-$Days)
        $logFiles = $logFiles | Where-Object { $_.LastWriteTime -ge $cutoffDate }
    }
    
    Write-ColorOutput "✅ Gefundene Dateien: $($logFiles.Count)" -Color "Green"
    $logFiles | ForEach-Object { Write-ColorOutput "  - $($_.Name) ($([math]::Round($_.Length/1KB, 2)) KB)" -Color "Gray" }
    
    # Lade und verarbeite Events
    Write-ColorOutput "`n📊 Lade Process-Events..." -Color "Yellow"
    $allEvents = Get-ProcessEvents -FilePaths $logFiles
    
    # Filtere auf ProcessMonitor-Events
    $processEvents = $allEvents | Where-Object {
        ($_.SourceContext -like "*ProcessMonitorWorker*") -and 
        ($_.EventType -in @("Start", "Stop"))
    }
    
    Write-ColorOutput "📈 Gesamte Process-Events: $($processEvents.Count)" -Color "Green"
    
    # Anwenden der Filter
    $filteredEvents = $processEvents
    
    if ($ProcessName) {
        $filteredEvents = $filteredEvents | Where-Object { $_.ProcessName -like "*$ProcessName*" }
        Write-ColorOutput "🔍 Nach Prozessname '$ProcessName' gefiltert: $($filteredEvents.Count)" -Color "Blue"
    }
    
    if ($EventType -ne "All") {
        $filteredEvents = $filteredEvents | Where-Object { $_.EventType -eq $EventType }
        Write-ColorOutput "🔍 Nach Event-Typ '$EventType' gefiltert: $($filteredEvents.Count)" -Color "Blue"
    }
    
    # Ergebnisse anzeigen/exportieren
    if ($filteredEvents.Count -eq 0) {
        Write-ColorOutput "⚠️  Keine Events entsprechen den Filterkriterien" -Color "Yellow"
    }
    else {
        $formattedEvents = $filteredEvents | ForEach-Object { Format-ProcessEvent $_ }
        
        if ($Export) {
            $exportPath = "ProcessEvents_$(Get-Date -Format 'yyyyMMdd_HHmmss').csv"
            $formattedEvents | Export-Csv -Path $exportPath -NoTypeInformation -Encoding UTF8
            Write-ColorOutput "💾 Events exportiert nach: $exportPath" -Color "Green"
        }
        elseif ($ForPipeline) {
            $formattedEvents
        }
        elseif ($OnlyProcessRuntimes) {
            ($formattedEvents | Sort-object -Property TimeStamp |  Group-Object -Property ProcessId, ProcessName | Where-Object Count -ge 2).Group | ForEach-Object {
                if ($_.EventType -eq "Start") {
                    $Start = [DateTime]$_.TimeStamp
                }
                elseif ($_.EventType -eq "Stop") {
                    $Ende = [DateTime]$_.TimeStamp; 
                    if (($Start -ge $Ende) -or ($Start -eq $null)) {
                        Write-Error("Ende des Prozesses vor Start oder Start des Prozesses wurde nicht gefunden. Das kann nicht sein. Überspringe Eintrag"); 
                        $Ende = $null 
                    } 
                }; 
                if ($Start -and $Ende) { 
                    [PSCustomObject]@{
                        Prozess          = $_.ProcessName
                        ProcessId        = $_.ProcessId
                        Start            = $Start
                        Ende             = $Ende
                        RuntimeInSeconds = ($Ende - $Start).TotalSeconds
                    }
                    $Start = $null 
                    $Ende = $null
                } 
            }
        }
        else {
            Write-ColorOutput "`n📋 Gefilterte Events:" -Color "Cyan"
            $formattedEvents | Format-Table -AutoSize
            
            # Statistiken
            Write-ColorOutput "`n📊 Statistiken:" -Color "Cyan"
            $stats = $formattedEvents | Group-Object ProcessName | Sort-Object Count -Descending
            $stats | Select-Object Name, Count | Format-Table -AutoSize
        }
    }
}
catch {
    Write-ColorOutput "❌ Fehler: $($_.Exception.Message)" -Color "Red"
    exit 1
}

Write-ColorOutput "`n✅ Analyse abgeschlossen" -Color "Green"


