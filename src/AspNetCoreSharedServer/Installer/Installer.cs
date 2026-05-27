using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using AspNetCoreSharedServer.Util;

namespace AspNetCoreSharedServer.Services;

public class Installer
{
    public const string ServiceId = "aspnet-server";
    public const bool AllowOnlyRootToCreateApplications = Configuration.AllowOnlyRootToCreateApplications;
    public const string WwwData = Configuration.WwwData;
    public static Shell Shell => Shell.Standard;
    public static ILogger Log => Configuration.Current.Logger;

    public static string LogsPath
    {
        get
        {
            if (OSInfo.IsWindows) throw new PlatformNotSupportedException();
            else return $"/var/log/{ServiceId}";
        }
    }

    public static string GetRandomString(int length)
    {
        string ptrn = "abcdefghjklmnpqrstwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        StringBuilder sb = new StringBuilder();

        byte[] randomBytes = new byte[4];
        RNGCryptoServiceProvider rng = new RNGCryptoServiceProvider();
        rng.GetBytes(randomBytes);

        // Convert 4 bytes into a 32-bit integer value.
        int seed = (randomBytes[0] & 0x7f) << 24 |
                    randomBytes[1] << 16 |
                    randomBytes[2] << 8 |
                    randomBytes[3];


        Random rnd = new Random(seed);

        for (int i = 0; i < length; i++)
            sb.Append(ptrn[rnd.Next(ptrn.Length - 1)]);

        return sb.ToString();
    }

    public static bool UserExists(string username) => Unix.getpwnam(username) != IntPtr.Zero;

    public static async Task AddUnixGroup(string group)
    {

        if (OSInfo.OSFlavor == OSFlavor.Alpine)
        {
            await Shell.ExecAsync($"addgroup {group}");
        }
        else
        {
            await Shell.ExecAsync($"groupadd {group}");
        }
    }
    public static async Task AddUnixUser(string user, string group, string password)
    {
        if (!UserExists(user))
        {
            if (OSInfo.OSFlavor == OSFlavor.Alpine)
            {
                await Shell.ExecAsync($"adduser -h /home/{user} -G {group} -D -s /bin/false {user}");
            }
            else
            {
                await Shell.ExecAsync($"useradd --home /home/{user} --gid {group} -m --shell /bin/false {user}");
            }

            var shell = Shell.ExecAsync($"passwd {user}");
            shell.Input.WriteLine(password);
            shell.Input.WriteLine(password);

            if (user == WwwData)
            {
                if (!Directory.Exists("/var/www/.dotnet"))
                {
                    Directory.CreateDirectory("/var/www/.dotnet");
                    await Shell.ExecAsync($"chown -R {user}:{group} /var/www/.dotnet");
                    await Shell.ExecAsync($"chmod -R 755 /var/www/.dotnet");
                }
            }

            Log.LogInformation($"Added system user {user}.");
        }
    }
    static ServiceController serviceController = null;
    public static ServiceController ServiceController
    {
        get
        {
            if (serviceController == null)
            {
                if (OSInfo.IsSystemd) serviceController = new SystemdServiceController();
                else if (OSInfo.IsOpenRC) serviceController = new OpenRCServiceController();
                else if (OSInfo.IsMac) serviceController = new LaunchdServiceController();
                else throw new NotSupportedException("Only Systemd, OpenRC & Launchd service controllers are supported.");
            }
            return serviceController;
        }
    }

    public static async Task InstallService(ServiceDescription description)
    {
        var service = ServiceController.Install(description);
        service.Enable();
        var status = service.Info;
        if (status != null && status.Status == OSServiceStatus.Running) service.Restart();
        else service.Start();

        const int MaxTry = 10;
        int n = 0;
        while (service.Info.Status != OSServiceStatus.Running && n++ < MaxTry)
        {
            Console.WriteLine($"Problems starting service {description.ServiceId}, retry for {MaxTry + 1 - n} seconds");
            service.Start();
            await Task.Delay(1000);
        }

        if (service.Info.Status != OSServiceStatus.Running)
        {
            //Environment.Exit(9);
            throw new Exception($"Failed to start {description.ServiceId} service.");
        }

        Console.WriteLine($"Installed system service {description.ServiceId}");
    }

    public static async Task Install()
    {
        if (!File.Exists(Configuration.Current.ConfigPath))
        {
            await AddUnixGroup(WwwData);
            await AddUnixUser(WwwData, WwwData, GetRandomString(16));

            var configfile = Configuration.Current.ConfigPath;
            var configpath = Path.GetDirectoryName(configfile);
            if (!System.IO.Directory.Exists(configpath))
            {
                System.IO.Directory.CreateDirectory(configpath);
                Configuration.Current.Save();

                if (AllowOnlyRootToCreateApplications)
                {
                    Unix.SetFilePermissions(configpath,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                    Unix.SetFilePermissions(configfile,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
                else
                {
                    Unix.SetFilePermissions(configfile,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite);
                    Unix.SetFilePermissions(configpath,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute);
                }
                Unix.SetOwnerAndGroup(configpath, "root", WwwData);
            }

            var conf = Configuration.Current;
            try
            {
                using (var mutex = Configuration.Mutex)
                {
                    conf = conf.LoadOnly(true, false);
                    conf.EnableHttp3 = false;
                    conf.User = WwwData;
                    conf.IdleTimeout = TimeSpan.FromMinutes(5);
                    conf.Recycle = TimeSpan.FromHours(29);
                    conf.Group = null;
                    conf.Save(false, false);
                }
            }
            catch (Exception ex)
            {
                throw;
            }
        }
        const string Description = "ASP.NET Core Shared Server support for shared hosting of ASP.NET Core applications";
        string Command;
        if (!OSInfo.IsMac) Command = $"/root/.dotnet/tools/{ServiceId}";
        else Command = $"/var/bin/{ServiceId}";
        string Directory = Path.GetDirectoryName(Command);

        ServiceDescription service;
        if (OSInfo.IsSystemd)
        {
            service = new SystemDServiceDescription()
            {
                ServiceId = ServiceId,
                Description = Description,
                Executable = Command,
                Directory = Directory,
                DependsOn = new[] { "network-online.target" },
                Environment = new Dictionary<string, string>()
            {
                { "ASPNETCORE_ENVIRONMENT", "Production" }
            },
                Restart = "on-failure",
                RestartSec = "1s",
                StartLimitBurst = "5",
                StartLimitIntervalSec = "500",
                User = "root",
                Group = "root",
                SyslogIdentifier = ServiceId
            };
        }
        else if (OSInfo.IsOpenRC)
        {
            var rcservice = new OpenRCServiceDescription()
            {
                ServiceId = ServiceId,
                Description = Description,
                Environment = new Dictionary<string, string>()
            {
                { "ASPNETCORE_ENVIRONMENT", "Production" }
            },
                CommandUser = "root",
                Command = Command,
                WorkingDirectory = Directory,
                CommandBackground = true,
                PidFile = $"/run/{ServiceId}.pid",
                OutputLog = $"/var/log/aspnet-server.log",
                ErrorLog = $"/var/log/aspnet-server.err",
                StopTimeout = 30
            };
            if (!OSInfo.IsWSL) rcservice.Need = "net";
            service = rcservice;
        }
        else if (OSInfo.IsMac)
        {
            var log = Path.Combine(LogsPath, $"{ServiceId}.log");
            service = new LaunchdServiceDescription()
            {
                Label = ServiceId,
                Executable = Command,
                WorkingDirectory = Directory,
                Environment = new Dictionary<string, string>()
            {
                { "ASPNETCORE_ENVIRONMENT", "Production" },
                { "PATH", Environment.GetEnvironmentVariable("PATH") }
            },
                ExitTimeout = 30,
                KeepAlive = true,
                RunAtLoad = true,
                StandardOutPath = log,
                StandardErrorPath = log,
                StartOnMount = true
            };
        }
        else throw new PlatformNotSupportedException("Only Systemd, OpenRC and Launchd are supported.");

        await InstallService(service);
    }

    public static async Task Uninstall()
    {
        ServiceController.Stop(ServiceId);
        ServiceController.Remove(ServiceId);
        Console.WriteLine("Uninstalled aspnet-server");
    }
}
