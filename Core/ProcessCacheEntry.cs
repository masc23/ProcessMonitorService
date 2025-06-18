namespace ProcessMonitorService.Core;

public class ProcessCacheEntry
{
    public string   Sid        { get; set; } = string.Empty;
    public DateTime LastAccess { get; set; } = DateTime.UtcNow;

    public void UpdateAccess()
    {
        LastAccess = DateTime.UtcNow;
    }
}
