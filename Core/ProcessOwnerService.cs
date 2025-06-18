using Microsoft.Management.Infrastructure;

namespace ProcessMonitorService.Core;

public class ProcessOwnerService(ILogger<ProcessOwnerService> logger) : IProcessOwnerService, IDisposable
{
    private readonly Lazy<CimSession> _cimSession = new(() => CimSession.Create(null));
    private bool _disposed;

    public async Task<string> GetProcessOwnerSidAsync(int processId)
    {
        try
        {
            return await Task.Run(() =>
            {
                var query = $"SELECT * FROM Win32_Process WHERE ProcessId = {processId}";
                var result = _cimSession.Value.QueryInstances(@"root\cimv2", "WQL", query).FirstOrDefault();

                if (result == null)
                {
                    logger.LogError("Process with ID {ProcessId} not found.", processId);
                    return "UNKNOWN_PROCESS_NOT_FOUND";
                }

                try
                {
                    var methodResult = _cimSession.Value.InvokeMethod(result, "GetOwnerSid", null);
                    return methodResult?.OutParameters?["Sid"]?.Value?.ToString() ?? "UNKNOWN_SID_NOT_FOUND";
                }
                catch (CimException cimEx)
                {
                    logger.LogError(cimEx, "CIM method 'GetOwnerSid' failed for process {ProcessId}", processId);
                    return "ERROR_GETTING_SID";
                }
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error getting SID for process {ProcessId}", processId);
            return "ERROR_GETTING_SID";
        }
    }

    public async Task<string> GetProcessNameByIdAsync(uint processId)
    {
        if (processId == 0) return "N/A";

        try
        {
            return await Task.Run(() =>
            {
                var query = $"SELECT Name FROM Win32_Process WHERE ProcessId = {processId}";
                var result = _cimSession.Value.QueryInstances(@"root\cimv2", "WQL", query).FirstOrDefault();
                return result?.CimInstanceProperties["Name"]?.Value?.ToString() ?? "N/A";
            });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "CIM query for process name failed for PID {ProcessId}", processId);
            return "ERROR_GETTING_PROCESS_NAME";
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed && disposing && _cimSession.IsValueCreated)
        {
            _cimSession.Value.Dispose();
        }

        _disposed = true;
    }
}
