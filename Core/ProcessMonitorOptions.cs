namespace ProcessMonitorService.Core;

public class ProcessMonitorOptions
{
    public List<string> ProcessFilters              { get; set; } = new();
    public List<string> ProcessExcludeFilters       { get; set; } = new();
    public int          CacheExpiryMinutes          { get; set; } = 30;
    public int          StatusUpdateIntervalMinutes { get; set; } = 5;
    public int          CacheCleanupIntervalMinutes { get; set; } = 10;
}
