namespace ProcessMonitorService.Core;

public class ProcessCacheEntry
{
    public string   Sid        { get; init; } = string.Empty;
    public DateTime LastAccess { get; }       = DateTime.UtcNow;
}
