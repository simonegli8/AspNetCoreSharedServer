using AspNetCoreSharedServer.Log;
using AspNetCoreSharedServer.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System.Net.Sockets;
using System.Reflection;

namespace AspNetCoreSharedServer;

public class Program
{
    public static void PrintVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        Console.WriteLine($"aspnet-server {assembly.GetName()?.Version?.ToString(3) ?? "1.0.0"}");
    }
    public static async Task Main(string[] args)
    {
        try
        {
            if (args.Contains("-v", StringComparer.OrdinalIgnoreCase) ||
                args.Contains("version", StringComparer.OrdinalIgnoreCase))
            {
                PrintVersion();
                return;
            }
            if (args.Contains("install", StringComparer.OrdinalIgnoreCase))
            {
                await Installer.Install();
                return;
            }
            if (args.Contains("uninstall", StringComparer.OrdinalIgnoreCase))
            {
                await Installer.Uninstall();
                return;
            }
            if (args.Contains("start", StringComparer.OrdinalIgnoreCase))
            {
                Installer.ServiceController.Start(Installer.ServiceId);
                if (Installer.ServiceController.Info(Installer.ServiceId)?.Status == OSServiceStatus.Running)
                {
                    Console.WriteLine("aspnet-server started");
                } else
                {
                    Console.WriteLine("Failed to start aspnet-server");
                }
                return;
            }
            if (args.Contains("stop", StringComparer.OrdinalIgnoreCase))
            {
                Installer.ServiceController.Stop(Installer.ServiceId);
                if (Installer.ServiceController.Info(Installer.ServiceId)?.Status == OSServiceStatus.Stopped)
                {
                    Console.WriteLine("aspnet-server stopped");
                }
                else
                {
                    Console.WriteLine("Failed to stop aspnet-server");
                }
                return;
            }
            if (args.Contains("enable", StringComparer.OrdinalIgnoreCase))
            {
                Installer.ServiceController.Enable(Installer.ServiceId);
                Console.WriteLine("aspnet-server enabled");
                return;
            }
            if (args.Contains("disable", StringComparer.OrdinalIgnoreCase))
            {
                Installer.ServiceController.Disable(Installer.ServiceId);
                Console.WriteLine("aspnet-server disabled");
                return;
            }

            if (args.Contains("restart", StringComparer.OrdinalIgnoreCase))
            {
                Installer.ServiceController.Restart(Installer.ServiceId);
                if (Installer.ServiceController.Info(Installer.ServiceId)?.Status == OSServiceStatus.Running)
                {
                    Console.WriteLine("aspnet-server restarted");
                }
                else
                {
                    Console.WriteLine("Failed to restart aspnet-server");
                }
                return;
            }
            if (args.Contains("status", StringComparer.OrdinalIgnoreCase))
            {
                var info = Installer.ServiceController.Info(Installer.ServiceId);
                Console.WriteLine(info.Status.ToString());
                return;
            }
            if (args.Any(a => a.Contains("help")) || args.Any(a => a.Contains("?")) ||
                args.Length == 1 && !args[0].EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                args.Length > 1)
            {
                PrintVersion();
                Console.WriteLine(@"
aspnet-server usage:

aspnet-server argument

possible values for argument:
- No argument: Start the server normally.
- -v or version: Show server version.
- install: Installs the server as a system service on Systemd 
  Linux or OpenRC Linux or macOS.
- uninstall: Uninstalls the system service.
- start: Starts the system service.
- stop: Stops the system service.
- restart: Restarts the system service.
- enable: Enables the system service.
- disable: Disables the system service.
- status: Shows the status of the service:");
                return;
            }
        }
        catch (Exception e)
        {
            Console.WriteLine($"Error: {e.Message}");
            return;
        }

        PrintVersion();

        var config = Configuration.Current;
        config = config.LoadOnly();

        var builder = Host.CreateApplicationBuilder(args);
        if (config.Syslog != null)
        {
            builder.Logging.AddProvider(
                new SyslogLoggerProvider(config.Syslog.Protocol, config.Syslog.Host, config.Syslog.Port ?? 514));
        }
        builder.Services.AddHostedService<Worker>();

        try
        {
            var host = builder.Build();
            host.Run();
            Console.WriteLine("Exit");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Unahndled exception {ex}");
        }
    }
}