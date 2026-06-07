using AspNetCoreSharedServer.Log;
using AspNetCoreSharedServer.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System.Diagnostics;
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
            if (args.Length > 0 && args[0].EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) args = args.Skip(1).ToArray();

            if (args.Contains("-debug", StringComparer.OrdinalIgnoreCase))
            {
                args = args.Where(arg => !arg.Equals("-debug", StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                Console.WriteLine("Please attach debugger...");
                while (!Debugger.IsAttached) Thread.Sleep(200);
                Debugger.Break();
            }
            if (args.Length > 0)
            {
                if (args[0].Equals("-v", StringComparison.OrdinalIgnoreCase) ||
                    args[0].Equals("version", StringComparison.OrdinalIgnoreCase))
                {
                    PrintVersion();
                    return;
                }
                if (args[0].Equals("install", StringComparison.OrdinalIgnoreCase))
                {
                    await Installer.Install();
                    return;
                }
                if (args[0].Equals("uninstall", StringComparison.OrdinalIgnoreCase))
                {
                    await Installer.Uninstall();
                    return;
                }
                if (args[0].Equals("start", StringComparison.OrdinalIgnoreCase))
                {
                    if (args.Length == 1)
                    {
                        Installer.ServiceController.Start(Installer.ServiceId);
                        if (Installer.ServiceController.Info(Installer.ServiceId)?.Status == OSServiceStatus.Running)
                        {
                            Console.WriteLine("aspnet-server started");
                        }
                        else
                        {
                            Console.WriteLine("Failed to start aspnet-server");
                        }
                    }
                    else
                    {
                        var apppool = args[1];
                        using var mutex = await AspServer.LockAsync();
                        try
                        {
                            var conf = await AspServer.Configuration.LoadConfigAsync();
                            var app = conf.Applications?[apppool];
                            if (app == null)
                            {
                                Console.WriteLine($"Application pool {apppool} not found.");
                            }
                            else
                            {
                                app.Offline = false;
                                await conf.SaveAsync(false, false);
                                Console.WriteLine($"Started {apppool} application pool.");
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Error starting application pool: {ex.Message}");
                        }
                    }
                    return;
                }
                if (args[0].Equals("stop", StringComparison.OrdinalIgnoreCase))
                {
                    if (args.Length == 1)
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
                    }
                    else
                    {
                        var apppool = args[1];
                        using var mutex = await AspServer.LockAsync();
                        try
                        {
                            var conf = await AspServer.Configuration.LoadConfigAsync();
                            var app = conf.Applications?[apppool];
                            if (app == null)
                            {
                                Console.WriteLine($"Application pool {apppool} not found.");
                            }
                            else
                            {
                                app.Offline = true;
                                await conf.SaveAsync(false, false);
                                Console.WriteLine($"Stopped {apppool} application pool.");
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Error stopping application pool: {ex.Message}");
                        }
                    }
                    return;
                }
                if (args[0].Equals("enable", StringComparison.OrdinalIgnoreCase))
                {
                    Installer.ServiceController.Enable(Installer.ServiceId);
                    Console.WriteLine("aspnet-server enabled");
                    return;
                }
                if (args[0].Equals("disable", StringComparison.OrdinalIgnoreCase))
                {
                    var stopped = () => Installer.ServiceController.Info(Installer.ServiceId)?.Status == OSServiceStatus.Stopped;
                    if (!stopped())
                    {
                        Installer.ServiceController.Stop(Installer.ServiceId);
                        if (stopped())
                        {
                            Console.WriteLine("aspnet-server stopped");
                        }
                        else
                        {
                            Console.WriteLine("Failed to stop aspnet-server");
                            return;
                        }
                    }
                    Installer.ServiceController.Disable(Installer.ServiceId);
                    Console.WriteLine("aspnet-server disabled");
                    return;
                }

                if (args[0].Equals("restart", StringComparison.OrdinalIgnoreCase))
                {
                    if (args.Length == 1)
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
                    }
                    else
                    {
                        var apppool = args[1];
                        using (var mutex = await AspServer.LockAsync())
                        {
                            try
                            {
                                var conf = await AspServer.Configuration.LoadConfigAsync();
                                var app = conf.Applications?[apppool];
                                if (app == null)
                                {
                                    Console.WriteLine($"Application pool {apppool} not found.");
                                    return;
                                }
                                else
                                {
                                    app.Offline = false;
                                    await conf.SaveAsync(false, false);
                                }
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"Error stopping application pool: {ex.Message}");
                            }
                        }
                        Thread.Sleep(200);
                        using (var mutex = await AspServer.LockAsync())
                        {
                            try
                            {
                                var conf = AspServer.Configuration.LoadConfig();
                                var app = conf.Applications?[apppool];
                                if (app == null)
                                {
                                    Console.WriteLine($"Application pool {apppool} not found.");
                                }
                                else
                                {
                                    app.Offline = false;
                                    await conf.SaveAsync(false, false);
                                    Console.WriteLine($"Restarted {apppool} application pool.");
                                }
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"Error starting application pool: {ex.Message}");
                            }
                        }
                        return;
                    }
                }

                if (args[0].Equals("status", StringComparison.OrdinalIgnoreCase))
                {
                    if (args.Length == 1)
                    {
                        var info = Installer.ServiceController.Info(Installer.ServiceId);
                        Console.WriteLine(info.Status.ToString());
                    }
                    else
                    {
                        var apppool = args[1];
                        using var mutex = await AspServer.LockAsync();
                        try
                        {
                            var conf = AspServer.Configuration.LoadConfig();
                            var app = conf.Applications?[apppool];
                            if (app == null)
                            {
                                Console.WriteLine($"Application pool {apppool} not found.");
                            }
                            else
                            {
                                Console.WriteLine($"Application pool {apppool} is {app.Status}.");
                                if (!string.IsNullOrEmpty(app.Error)) Console.WriteLine($"Error: {app.Error}");
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Error fetching application pool status: {ex.Message}");
                        }
                    }
                    return;
                }

                PrintVersion();
                Console.WriteLine(@"
aspnet-server usage:

aspnet-server argument [argument2]

possible values for argument:
- No argument: Start the server normally.
- -v or version: Show server version.
- install: Installs the server as a system service on Systemd 
  Linux or OpenRC Linux or macOS.
- uninstall: Uninstalls the system service.
- start: Starts the system service, or the application pool if argument2 is specified.
- stop: Stops the system service, or the application pool if argument2 is specified.
- restart: Restarts the system service, or the application pool if argument2 is specified.
- enable: Enables the system service.
- disable: Disables the system service.
- status: Shows the status of the service, or the application pool if argument2 is specified.:");
                return;
            }
        }
        catch (Exception e)
        {
            Console.WriteLine($"Error: {e.Message}");
            return;
        }

        PrintVersion();

        Configuration config = Configuration.Current;
        try
        {
            using (await Configuration.LockAsync())
            {
                config = await config.LoadConfigAsync(false);
            }
        }
        catch (Exception e) { }

        var settings = new HostApplicationBuilderSettings
        {
            DisableDefaults = true,
            Args = args
        };

        var builder = Host.CreateApplicationBuilder(settings);

        builder.Configuration.AddJsonFile(
            "appsettings.json",
            optional: true,
            reloadOnChange: false);

        builder.Logging.AddConfiguration(
            builder.Configuration.GetSection("Logging"));

        builder.Logging.AddConsole();

        // var builder = Host.CreateApplicationBuilder(args);
        if (config.Syslog != null)
        {
            builder.Logging.AddProvider(
                new SyslogLoggerProvider(config.Syslog.Protocol, config.Syslog.Host, config.Syslog.Port ?? 514));
        }
        builder.Services.AddHostedService<Worker>();

        try
        {
            var host = builder.Build();
            await host.RunAsync();
            //Console.WriteLine("Exit");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Unahndled exception {ex}");
        }
    }
}