using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Management;

namespace ProcessMonitorService.Core;

public class ProcessMonitorWorker : BackgroundService
{
    private readonly ILogger<ProcessMonitorWorker> _logger;
    private readonly IOptionsMonitor<ProcessMonitorOptions> _optionsMonitor;
    private readonly IProcessOwnerService _processOwnerService;

    private HashSet<string> _processFilterSet = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _processExcludeFilterSet = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<int, ProcessCacheEntry> _processSidCache = new();
    private readonly object _filterUpdateLock = new object();
    private string _lastFilterSnapshot = "";

    private ManagementEventWatcher? _startWatcher;
    private ManagementEventWatcher? _stopWatcher;
    private Timer? _cacheCleanupTimer;
    private Timer? _statusTimer;

    private ProcessMonitorOptions _currentOptions;
    private bool _disposed = false;

    public ProcessMonitorWorker(
        ILogger<ProcessMonitorWorker> logger,
        IOptionsMonitor<ProcessMonitorOptions> optionsMonitor,
        IProcessOwnerService processOwnerService)
    {
        _logger = logger;
        _optionsMonitor = optionsMonitor;
        _processOwnerService = processOwnerService;
        _currentOptions = _optionsMonitor.CurrentValue;

        UpdateProcessFilters(_currentOptions.ProcessFilters, _currentOptions.ProcessExcludeFilters);

        // Configuration change handler
        _optionsMonitor.OnChange(options =>
        {
            _currentOptions = options;
            UpdateProcessFilters(_currentOptions.ProcessFilters, _currentOptions.ProcessExcludeFilters);
        });
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("=== ProcessMonitorWorker starting ===");
        _logger.LogInformation("Base directory: {BaseDirectory}", AppContext.BaseDirectory);
        _logger.LogInformation("User context: {UserName}", Environment.UserName);
        _logger.LogInformation("Active process filters: {FilterCount}, exclude filters: {ExcludeFilterCount}",
            _processFilterSet.Count, _processExcludeFilterSet.Count);

        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await InitializeWmiWatchersAsync(stoppingToken);
            InitializeTimers();

            _logger.LogInformation("✓ ProcessMonitorWorker successfully started");

            // Keep service running
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("ProcessMonitorWorker stopping gracefully");
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "CRITICAL ERROR in ProcessMonitorWorker. Service may become unstable");
            throw; // Re-throw to trigger service restart
        }
    }

    private async Task InitializeWmiWatchersAsync(CancellationToken cancellationToken)
    {
        await Task.Run(() =>
        {
            try
            {
                // Dynamische WQL-Query basierend auf den konfigurierten Filtern
                string processFilter = BuildProcessFilter();

                string baseQuery = string.IsNullOrEmpty(processFilter)
                    ? "TargetInstance ISA 'Win32_Process'"
                    : $"TargetInstance ISA 'Win32_Process' AND ({processFilter})";

                string creationQuery = $"SELECT * FROM __InstanceCreationEvent WITHIN 1 WHERE {baseQuery}";
                string deletionQuery = $"SELECT * FROM __InstanceDeletionEvent WITHIN 1 WHERE {baseQuery}";

                _startWatcher = new ManagementEventWatcher(new WqlEventQuery(creationQuery));
                _stopWatcher = new ManagementEventWatcher(new WqlEventQuery(deletionQuery));

                _startWatcher.EventArrived += OnProcessStarted;
                _stopWatcher.EventArrived += OnProcessStopped;

                _logger.LogInformation("Starting WMI event watchers with filter: {ProcessFilter}",
                    string.IsNullOrEmpty(processFilter) ? "ALL PROCESSES" : processFilter);
                _startWatcher.Start();
                _stopWatcher.Start();
                _logger.LogInformation("✓ WMI event watchers started successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize WMI watchers");
                throw;
            }
        }, cancellationToken);
    }

    private string BuildProcessFilter()
    {
        var includeConditions = new List<string>();
        var excludeConditions = new List<string>();

        // Include-Filter erstellen (wenn vorhanden)
        if (_processFilterSet.Any())
        {
            var conditions = _processFilterSet
                .Select(processName =>
                {
                    if (processName.Contains('*') || processName.Contains('?'))
                    {
                        var wqlPattern = processName.Replace("*", "%").Replace("?", "_");
                        return $"TargetInstance.Name LIKE '{wqlPattern}'";
                    }
                    else
                    {
                        return $"TargetInstance.Name = '{processName}'";
                    }
                });

            includeConditions.Add($"({string.Join(" OR ", conditions)})");
        }

        // Exclude-Filter erstellen (wenn vorhanden)
        if (_processExcludeFilterSet.Any())
        {
            var conditions = _processExcludeFilterSet
                .Select(processName =>
                {
                    if (processName.Contains('*') || processName.Contains('?'))
                    {
                        var wqlPattern = processName.Replace("*", "%").Replace("?", "_");
                        return $"NOT TargetInstance.Name LIKE '{wqlPattern}'";
                    }
                    else
                    {
                        return $"NOT TargetInstance.Name = '{processName}'";
                    }
                });

            excludeConditions.AddRange(conditions);
        }

        // Finale Bedingung zusammenbauen
        var finalConditions = new List<string>();

        if (includeConditions.Any())
        {
            finalConditions.AddRange(includeConditions);
        }

        if (excludeConditions.Any())
        {
            finalConditions.AddRange(excludeConditions);
        }

        // Wenn keine Filter vorhanden sind, alle Prozesse überwachen
        if (!finalConditions.Any())
        {
            return string.Empty;
        }

        return string.Join(" AND ", finalConditions);
    }

    private void UpdateProcessFilters(List<string> filters, List<string> excludeFilters)
    {
        _logger.LogDebug("Entering UpdateProcessFilters.");
        lock (_filterUpdateLock)
        {
            string snapshot = string.Join(",", filters) + "|" + string.Join(",", excludeFilters);
            _logger.LogDebug("snapshot            {snapshot}", snapshot);
            _logger.LogDebug("_lastFilterSnapshot {_lastFilterSnapshot}", _lastFilterSnapshot);
            if (snapshot == _lastFilterSnapshot)
            {
                _logger.LogDebug("Configuration snapshot identical, skipping update. Last snapshot: {LastSnapshot}", _lastFilterSnapshot);
                return;
            }

            _logger.LogInformation("Configuration changed, reloading settings");

            _lastFilterSnapshot = snapshot;

            _processFilterSet = new HashSet<string>(filters ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            _processExcludeFilterSet = new HashSet<string>(excludeFilters ?? new List<string>(), StringComparer.OrdinalIgnoreCase);

            _logger.LogInformation("Process filters updated: {FilterCount} include filters, {ExcludeFilterCount} exclude filters loaded",
                _processFilterSet.Count, _processExcludeFilterSet.Count);
            _logger.LogDebug("Active include filters: {@ProcessFilters}", _processFilterSet.ToArray());
            _logger.LogDebug("Active exclude filters: {@ProcessExcludeFilters}", _processExcludeFilterSet.ToArray());

            // WMI-Watcher neu initialisieren, wenn sie bereits laufen
            if (_startWatcher != null || _stopWatcher != null)
            {
                _logger.LogInformation("Reinitializing WMI watchers due to filter change");
                RestartWatchers();
            }
        }
    }

    private void RestartWatchers()
    {
        try
        {
            // Alte Watcher stoppen und dispose
            _startWatcher?.Stop();
            _stopWatcher?.Stop();
            _startWatcher?.Dispose();
            _stopWatcher?.Dispose();

            // Neu initialisieren
            InitializeWmiWatchersAsync(CancellationToken.None).Wait();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error restarting WMI watchers");
        }
    }
    private void InitializeTimers()
    {
        // Cache cleanup timer
        var cleanupInterval = TimeSpan.FromMinutes(_currentOptions.CacheCleanupIntervalMinutes);
        _cacheCleanupTimer = new Timer(CleanupExpiredCacheEntries, null, cleanupInterval, cleanupInterval);

        // Status update timer
        var statusInterval = TimeSpan.FromMinutes(_currentOptions.StatusUpdateIntervalMinutes);
        _statusTimer = new Timer(LogStatusUpdate, null, statusInterval, statusInterval);

        _logger.LogInformation("Timers initialized - Cache cleanup: {CleanupInterval}min, Status: {StatusInterval}min",
            _currentOptions.CacheCleanupIntervalMinutes, _currentOptions.StatusUpdateIntervalMinutes);
    }

    private void OnProcessStarted(object sender, EventArrivedEventArgs e)
    {
        try
        {
            _ = Task.Run(async () => await HandleProcessEventAsync(e, "Start"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in process start event handler");
        }
    }

    private void OnProcessStopped(object sender, EventArrivedEventArgs e)
    {
        try
        {
            _ = Task.Run(async () => await HandleProcessEventAsync(e, "Stop"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in process stop event handler");
        }
    }

    private async Task HandleProcessEventAsync(EventArrivedEventArgs e, string eventType)
    {
        try
        {
            var process = (ManagementBaseObject)e.NewEvent["TargetInstance"];
            var name = process["Name"]?.ToString() ?? "N/A";

            // Filter check - early return if not monitored
            if (!ShouldMonitorProcess(name))
            {
                return;
            }

            var pid = Convert.ToInt32(process["ProcessId"]);
            var sid = "UNKNOWN";

            uint parentPid = Convert.ToUInt32(process["ParentProcessId"]);
            string parentName = await _processOwnerService.GetProcessNameByIdAsync(parentPid);

            if (eventType == "Start")
            {
                sid = await _processOwnerService.GetProcessOwnerSidAsync(pid);
                _processSidCache[pid] = new ProcessCacheEntry { Sid = sid };
            }
            else if (eventType == "Stop")
            {
                if (_processSidCache.TryRemove(pid, out var cachedEntry))
                {
                    sid = cachedEntry.Sid;
                }
            }

            // Rufen Sie die aktualisierte Logging-Methode mit den neuen Parametern auf
            LogProcessEvent(eventType, name, pid, sid, Convert.ToInt32(parentPid), parentName, process);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling process event: {EventType}", eventType);
        }
    }

    private bool ShouldMonitorProcess(string processName)
    {
        // Zuerst prüfen, ob der Prozess explizit ausgeschlossen werden soll
        if (_processExcludeFilterSet.Any())
        {
            foreach (var excludeFilter in _processExcludeFilterSet)
            {
                if (excludeFilter.Contains('*') || excludeFilter.Contains('?'))
                {
                    // Wildcard-Matching für Exclude-Filter
                    var pattern = "^" + System.Text.RegularExpressions.Regex.Escape(excludeFilter)
                        .Replace("\\*", ".*")
                        .Replace("\\?", ".") + "$";

                    if (System.Text.RegularExpressions.Regex.IsMatch(processName, pattern,
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    {
                        return false; // Prozess ist ausgeschlossen
                    }
                }
                else
                {
                    // Exakte Übereinstimmung für Exclude-Filter
                    if (string.Equals(processName, excludeFilter, StringComparison.OrdinalIgnoreCase))
                    {
                        return false; // Prozess ist ausgeschlossen
                    }
                }
            }
        }

        // Dann prüfen, ob Include-Filter definiert sind
        if (_processFilterSet.Any())
        {
            foreach (var includeFilter in _processFilterSet)
            {
                if (includeFilter.Contains('*') || includeFilter.Contains('?'))
                {
                    // Wildcard-Matching für Include-Filter
                    var pattern = "^" + System.Text.RegularExpressions.Regex.Escape(includeFilter)
                        .Replace("\\*", ".*")
                        .Replace("\\?", ".") + "$";

                    if (System.Text.RegularExpressions.Regex.IsMatch(processName, pattern,
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    {
                        return true; // Prozess ist eingeschlossen
                    }
                }
                else
                {
                    // Exakte Übereinstimmung für Include-Filter
                    if (string.Equals(processName, includeFilter, StringComparison.OrdinalIgnoreCase))
                    {
                        return true; // Prozess ist eingeschlossen
                    }
                }
            }
            return false; // Kein Include-Filter matched
        }

        // Keine Include-Filter definiert = alle Prozesse überwachen (außer ausgeschlossene)
        return true;
    }

    private void LogProcessEvent(string eventType, string name, int pid, string sid, int parentPid, string parentName, ManagementBaseObject process)
    {
        _logger.LogInformation(
            "Process {EventType}: {ProcessName} (PID: {ProcessId}) User: {UserSid} Parent: {ParentName} (PID: {ParentProcessId}) Path: {ExecutablePath} Command: {CommandLine}",
            eventType,
            name,
            pid,
            sid,
            parentName,
            parentPid,
            process["ExecutablePath"]?.ToString() ?? "N/A",
            process["CommandLine"]?.ToString() ?? "N/A"
        );
    }

    private void CleanupExpiredCacheEntries(object? state)
    {
        try
        {
            var expiryTime = TimeSpan.FromMinutes(_currentOptions.CacheExpiryMinutes);
            var cutoffTime = DateTime.UtcNow - expiryTime;

            var expiredKeys = _processSidCache
                .Where(kvp => kvp.Value.LastAccess < cutoffTime)
                .Select(kvp => kvp.Key)
                .ToList();

            var removedCount = 0;
            foreach (var key in expiredKeys)
            {
                if (_processSidCache.TryRemove(key, out _))
                {
                    removedCount++;
                }
            }

            if (removedCount > 0)
            {
                _logger.LogDebug("Cache cleanup: removed {RemovedCount} expired entries, {RemainingCount} entries remaining",
                    removedCount, _processSidCache.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during cache cleanup");
        }
    }

    private void LogStatusUpdate(object? state)
    {
        try
        {
            _logger.LogInformation("Service status: Running. Cache entries: {CacheCount}, Include filters: {FilterCount}, Exclude filters: {ExcludeFilterCount}",
                _processSidCache.Count, _processFilterSet.Count, _processExcludeFilterSet.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during status update");
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("=== ProcessMonitorWorker stopping ===");

        try
        {
            // Stop timers
            _cacheCleanupTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            _statusTimer?.Change(Timeout.Infinite, Timeout.Infinite);

            // Stop WMI watchers
            _startWatcher?.Stop();
            _stopWatcher?.Stop();

            _logger.LogInformation("✓ WMI event watchers stopped successfully");
            _logger.LogInformation("✓ Service stopped at: {StopTime}. Final cache count: {CacheCount}",
                DateTime.Now, _processSidCache.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during service shutdown");
        }

        await base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        if (!_disposed)
        {
            try
            {
                _startWatcher?.Dispose();
                _stopWatcher?.Dispose();
                _cacheCleanupTimer?.Dispose();
                _statusTimer?.Dispose();
                (_processOwnerService as IDisposable)?.Dispose();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error during disposal");
            }
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}
