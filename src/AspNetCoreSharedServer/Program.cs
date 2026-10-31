using AspNetCoreSharedServer.Log;
using AspNetCoreSharedServer.Services;
using EstrellasDeEsperanza.AsyncLock;
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
        Console.WriteLine($"aspnet-server {Server.Version}");
    }

    static AsyncMutexLock applockmutex = new AsyncMutexLock("AspNetCoreSharedServer.AssertOnlyOneInstance", MutexScope.Machine);
    static IDisposable? applock = null;
    
    private static async Task AssertOnlyOneInstance()
    {
        try
        {
            applock = await applockmutex.LockAsync(TimeSpan.Zero);
        } catch (TimeoutException)
        {
            Console.WriteLine("Another instance of aspnet-server is already running.");
            Environment.Exit(-10);
        }
        AppDomain.CurrentDomain.ProcessExit += (_,_) => applock.Dispose();
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
                if (args[0].Equals("create-user", StringComparison.OrdinalIgnoreCase))
                {
                    if (!OperatingSystem.IsWindows())
                    {
                        Console.Error.WriteLine("create-user is for Windows. On Linux and macOS create the user with useradd or dscl.");
                        Environment.ExitCode = 1;
                    }
                    else if (args.Length != 2)
                    {
                        Console.Error.WriteLine("Usage: aspnet-server create-user <name>");
                        Environment.ExitCode = 1;
                    }
                    else
                    {
                        // Needs administrator rights.
                        Console.WriteLine(WindowsAccounts.EnsureUser(args[1]) ?
                            $"Created the local user {args[1]}." : $"The local user {args[1]} exists already.");
                    }
                    return;
                }
                if (args[0].Equals("start", StringComparison.OrdinalIgnoreCase))
                {
                    if (args.Length == 1)
                    {
                        if (Installer.ServiceController.Info(Installer.ServiceId)?.Status == OSServiceStatus.Running)
                        {
                            Console.WriteLine("aspnet-server is already running.");
                        }
                        else
                        {
                            Installer.ServiceController.Start(Installer.ServiceId);
                            if (Installer.ServiceController.Info(Installer.ServiceId)?.Status == OSServiceStatus.Running)
                            {
                                Console.WriteLine("aspnet-server started.");
                            }
                            else
                            {
                                Console.WriteLine("Failed to start aspnet-server.");
                            }
                        }
                    }
                    else
                    {
                        var apppool = args[1];
                        using var mutex = await AspServer.LockAsync();
                        try
                        {
                            var conf = await AspServer.Configuration.LoadConfigAsync();
                            if (conf == null)
                            {
                                Console.WriteLine("Error loading configuration.");
                                return;
                            }
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
                        if (Installer.ServiceController.Info(Installer.ServiceId)?.Status == OSServiceStatus.Stopped)
                        {
                            Console.WriteLine("aspnet-server is already stopped.");
                        }
                        else
                        {
                            Installer.ServiceController.Stop(Installer.ServiceId);
                            if (Installer.ServiceController.Info(Installer.ServiceId)?.Status == OSServiceStatus.Stopped)
                            {
                                Console.WriteLine("aspnet-server stopped.");
                            }
                            else
                            {
                                Console.WriteLine("Failed to stop aspnet-server.");
                            }
                        }
                    }
                    else
                    {
                        var apppool = args[1];
                        using var mutex = await AspServer.LockAsync();
                        try
                        {
                            var conf = await AspServer.Configuration.LoadConfigAsync();
                            if (conf == null)
                            {
                                Console.WriteLine("Error loading configuration.");
                                return;
                            }
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
                                if (conf == null)
                                {
                                    Console.WriteLine("Error loading configuration.");
                                    return;
                                }
                                var app = conf.Applications?[apppool];
                                if (app == null)
                                {
                                    Console.WriteLine($"Application pool {apppool} not found.");
                                    return;
                                }
                                else
                                {
                                    app.Offline = true;
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
                                if (conf == null)
                                {
                                    Console.WriteLine("Error loading configuration.");
                                    return;
                                }
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
                        Console.WriteLine(info?.Status.ToString());
                    }
                    else
                    {
                        var apppool = args[1];
                        using var mutex = await AspServer.LockAsync();
                        try
                        {
                            var conf = AspServer.Configuration.LoadConfig();
                            if (conf == null)
                            {
                                Console.WriteLine("Error loading configuration.");
                                return;
                            }
                            if (apppool != "all")
                            {
                                var app = conf.Applications?[apppool];
                                if (app == null)
                                {
                                    Console.WriteLine($"Application pool {apppool} not found.");
                                }
                                else
                                {
                                    Console.WriteLine($"Application pool {apppool} is {(app.Offline ? "offline" : $"online")} and its status is '{app.Status}'.");
                                    if (!string.IsNullOrEmpty(app.Error)) Console.WriteLine($"Error: {app.Error}");
                                }
                            } else
                            {
                                if (conf.Applications != null) 
                                {
                                    foreach (var app in conf.Applications)
                                    {
                                        Console.WriteLine($"Application pool {app.Name} is {(app.Offline ? "offline" : $"online")} and its status is '{app.Status}'.");
                                        if (!string.IsNullOrEmpty(app.Error)) Console.WriteLine($"Error: {app.Error}");
                                    }
                                } else
                                {
                                    Console.WriteLine("No applications defined.");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Error fetching application pool status: {ex.Message}");
                        }
                    }
                    return;
                }
                if (args[0].Equals("lock", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        using var mylock = Configuration.MutexLock.LockAsync(TimeSpan.Zero);
                        Console.WriteLine("aspnet-server is not locked.");
                    }
                    catch (Exception)
                    {
                        Console.WriteLine("aspnet-server is locked.");
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
- create-user <name>: (Windows) Creates the local user that an application runs as. Needs administrator rights.
- start: Starts the system service, or the application pool if argument2 is specified.
- stop: Stops the system service, or the application pool if argument2 is specified.
- restart: Restarts the system service, or the application pool if argument2 is specified.
- enable: Enables the system service.
- disable: Disables the system service.
- status: Shows the status of the service, or the application pool if argument2 is specified,
  or of all application pools if 'all' is specified as argument2.");
                return;
            }
        }
        catch (Exception e)
        {
            Console.WriteLine($"Error: {e.Message}");
            return;
        }

        PrintVersion();

        await AssertOnlyOneInstance();

        Configuration config = Configuration.Current;
        try
        {
            using (await Configuration.LockAsync())
            {
                config = await config.LoadConfigAsync(false) ?? throw new InvalidOperationException("The configuration could not be loaded.");
            }
        }
        catch (Exception) { }

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