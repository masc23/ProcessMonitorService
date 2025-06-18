namespace ProcessMonitorService.Core;

public class ProcessMonitorOptions
{
    public List<string> ProcessFilters        { get; init; } = [];
    public List<string> ProcessExcludeFilters { get; init; } = [];

    public int CacheExpiryMinutes          { get; init; } = 30;
    public int StatusUpdateIntervalMinutes { get; init; } = 5;
    public int CacheCleanupIntervalMinutes { get; init; } = 10;
}
