using ProcessMonitorService.Core;
using Serilog;
using System.Security.Principal;

namespace ProcessMonitorService;

internal static class Program
{
    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);

        // Prüft, ob der Benutzer in der Rolle "Administrator" ist.
        // Der "System"-Benutzer hat ebenfalls administrative Rechte und wird hier korrekt erfasst.
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static async Task Main(string[] args)
    {
        // Serilog Bootstrap-Logger für den Startvorgang
        Log.Logger = new LoggerConfiguration()
            .WriteTo.Console()
            .WriteTo.File(
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "ProcessMonitorService",
                    "logs",
                    $"startup_error_{DateTime.Now:yyyyMMdd_HHmmss}.log"
                ),
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}"
            )
            .CreateBootstrapLogger();

        try
        {
            Log.Information("=== Process Monitor Service starting ===");

            if (!IsAdministrator())
            {
                Log.Fatal("Application must be run with administrative privileges (or as System user). Exiting.");
                // Optional: Eine Fehlermeldung auf die Konsole schreiben, falls es kein Dienst ist
                if (Environment.UserInteractive)
                {
                    Console.WriteLine("Error: This application requires administrative privileges to run. Please run as administrator.");
                }
                return; // Beendet die Anwendung, wenn keine Admin-Rechte vorliegen
            }

            await Host.CreateDefaultBuilder(args)
                .UseWindowsService()
                .ConfigureAppConfiguration((context, config) =>
                {
                    config.SetBasePath(AppContext.BaseDirectory)
                        .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                        .AddJsonFile($"appsettings.{context.HostingEnvironment.EnvironmentName}.json", optional: true, reloadOnChange: true);
                })
                .UseSerilog((context, services, configuration) => configuration
                    .ReadFrom.Configuration(context.Configuration)
                    .ReadFrom.Services(services)
                    .Enrich.FromLogContext()
                    .Enrich.WithProperty("MachineName", Environment.MachineName)
                    .Enrich.WithProperty("ThreadId", Thread.CurrentThread.ManagedThreadId)
                )
                .ConfigureServices((hostContext, services) =>
                {
                    // Configuration
                    services.Configure<ProcessMonitorOptions>(
                        hostContext.Configuration.GetSection("ProcessMonitor"));

                    // Services
                    services.AddSingleton<IProcessOwnerService, ProcessOwnerService>();
                    services.AddHostedService<ProcessMonitorWorker>();

                    // Health checks (optional)
                    services.AddHealthChecks();
                })
                .Build()
                .RunAsync();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Critical error: Host could not be started");
            Environment.ExitCode = 1;
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }
}
