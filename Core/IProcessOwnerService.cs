namespace ProcessMonitorService.Core;

public interface IProcessOwnerService
{
    Task<string> GetProcessOwnerSidAsync(int processId);
    Task<string> GetProcessNameByIdAsync(uint processId);
}
