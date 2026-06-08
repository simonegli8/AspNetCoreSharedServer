using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using Mono.Cecil;
using EstrellasDeEsperanza.AsyncLock;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using AspNetCoreSharedServer.Util;
using System.Runtime.CompilerServices;

namespace AspNetCoreSharedServer;


[JsonConverter(typeof(StringEnumConverter))]
public enum Command
{
    None = 0,
    Shutdown = 1,
    Restart = 2
}

[JsonConverter(typeof(StringEnumConverter))]
public enum Status
{
    Stopped = 0,
    Error = 1,
    Stopping = 2,
    Starting = 3,
    Running = 4
}
public class Configuration
{
    public const bool AllowOnlyRootToCreateApplications = true;
    public const string WwwData = "www-data";
    public static readonly TimeSpan LockTimeout = TimeSpan.FromMinutes(2);
    public static readonly string LockName = $"EstrellasDeEsperanza{Path.DirectorySeparatorChar}aspnet-server";

    public class SyslogConfiguration
    {
        [DefaultValue(null)]
        public string? Host { get; set; } = null;
        [DefaultValue(null)]
        public int? Port { get; set; } = null;
        [DefaultValue(ProtocolType.Unknown)]
        public ProtocolType Protocol { get; set; } = ProtocolType.Unknown;

    }
    static int StartPort = 10000;
    public static async Task<int> FindFreePortAsync(bool temporary = false)
    {
        const int EndPort = 49000;
        int port = 0;
        Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            IPEndPoint localEP;
            if (temporary)
            {
                localEP = new IPEndPoint(IPAddress.Any, 0);
                socket.Bind(localEP);
            }
            else
            {
                await Configuration.Current.LoadAsync();
                var apps = Configuration.Current.Applications;
                port = StartPort;
                var usedPorts = new HashSet<int>(apps
                    .SelectMany(app => app.ListenUrls.Split(';')
                        .Where(url => !string.IsNullOrWhiteSpace(url))
                        .Select(url =>
                        {
                            int port = -1;
                            try
                            {
                                port = new Uri(url).Port;
                            }
                            catch { }
                            return port;
                        })
                        .Where(port => port != -1))
                    .Distinct());
                do
                {
                    try
                    {
                        if (usedPorts.Contains(port))
                        {
                            port++;
                            StartPort++;
                        }
                        else
                        {
                            localEP = new IPEndPoint(IPAddress.Any, port++);
                            socket.Bind(localEP);
                            StartPort++; // Increment the start port for the next call
                            break;
                        }
                    }
                    catch (SocketException)
                    {
                        if (port > EndPort) throw new InvalidOperationException("No free ports available in the range.");
                    }
                } while (true);
            }
            localEP = (IPEndPoint)socket.LocalEndPoint;
            port = localEP.Port;
        }
        finally
        {
            socket.Close();
        }
        return port;
    }
    public static int FindFreePort(bool temporary = false)
    {
        const int EndPort = 49000;
        int port = 0;
        Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            IPEndPoint localEP;
            if (temporary)
            {
                localEP = new IPEndPoint(IPAddress.Any, 0);
                socket.Bind(localEP);
            }
            else
            {
                Configuration.Current.Load();
                var apps = Configuration.Current.Applications;
                port = StartPort;
                var usedPorts = new HashSet<int>(apps
                    .SelectMany(app => app.ListenUrls.Split(';')
                        .Where(url => !string.IsNullOrWhiteSpace(url))
                        .Select(url =>
                        {
                            int port = -1;
                            try
                            {
                                port = new Uri(url).Port;
                            }
                            catch { }
                            return port;
                        })
                        .Where(port => port != -1))
                    .Distinct());
                do
                {
                    try
                    {
                        if (usedPorts.Contains(port))
                        {
                            port++;
                            StartPort++;
                        }
                        else
                        {
                            localEP = new IPEndPoint(IPAddress.Any, port++);
                            socket.Bind(localEP);
                            StartPort++; // Increment the start port for the next call
                            break;
                        }
                    }
                    catch (SocketException)
                    {
                        if (port > EndPort) throw new InvalidOperationException("No free ports available in the range.");
                    }
                } while (true);
            }
            localEP = (IPEndPoint)socket.LocalEndPoint;
            port = localEP.Port;
        }
        finally
        {
            socket.Close();
        }
        return port;
    }

    [JsonIgnore]
    public string ConfigPathOld => OSInfo.IsWindows ?
        Path.Combine(Environment.CurrentDirectory, "applications.json") :
        "/etc/aspnetcore/applications.json";
    [JsonIgnore]
    public string ConfigPath => OSInfo.IsWindows ?
        Path.Combine(Environment.CurrentDirectory, "configuration.json") :
        "/etc/aspnet-server/configuration.json";

#if Server
    [JsonIgnore]
    public ILogger<Worker> Logger { get; set; } = null;
#endif
    [DefaultValue(null)]
    public Applications? Applications { get; set; } = new Applications();
    public readonly static TimeSpan GlobalIdleTimeout = TimeSpan.FromMinutes(20);
    public readonly static TimeSpan GlobalRecycle = TimeSpan.FromHours(29);
    public TimeSpan? IdleTimeout { get; set; } = GlobalIdleTimeout;
    public TimeSpan? Recycle { get; set; } = GlobalRecycle;
    [DefaultValue(false)]
    public bool EnableHttp3 { get; set; } = true;
    [DefaultValue(null)]
    public string? User { get; set; } = null;
    [DefaultValue(null)]
    public string? Group { get; set; } = null;
    [DefaultValue(0.95)]
    public double MemoryLowThreshold { get; set; } = 0.95;
    [DefaultValue(null)]
    public TimeSpan? IdleTimeoutOnLowMemory { get; set; } = null;
    public static readonly TimeSpan DefaultIdleTimeoutOnLowMemory = TimeSpan.FromMinutes(1);

    [DefaultValue(null)]
    public SyslogConfiguration? Syslog { get; set; } = null;

    [DefaultValue(Command.None)]
    public Command Command { get; set; } = Command.None;
    public bool IsShuttingDown = false;
    [DefaultValue(false)]
    public bool Offline { get; set; } = false;
    [DefaultValue(typeof(TimeSpan), "00:05:00")]
    public TimeSpan FailureInterval { get; set; } = TimeSpan.FromMinutes(5);
    [DefaultValue(5)]
    public int FailureLimit { get; set; } = 5;
    int loadEntered = 0;

    static AsyncMutexLock? @lock = null;
    public static AsyncMutexLock MutexLock => @lock ??= new AsyncMutexLock(LockName, MutexScope.Machine);
    public static Task<IDisposable> LockAsync(CancellationToken cancel = default) =>
        MutexLock.LockAsync(CancellationTokenSource.CreateLinkedTokenSource(cancel,
            new CancellationTokenSource(LockTimeout).Token).Token);
    public static IDisposable Lock(CancellationToken cancel = default) =>
        MutexLock.LockAsync(CancellationTokenSource.CreateLinkedTokenSource(cancel,
            new CancellationTokenSource(LockTimeout).Token).Token);
    internal async Task<Configuration?> LoadConfigAsync(bool loadApps = true)
    {
        // import old config path /etc/aspnetcore if it exists to /etc/aspnet-server
        if (File.Exists(ConfigPathOld))
        {
            var path = Path.GetDirectoryName(ConfigPath);
            if (path != null && !Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
                if (!OSInfo.IsWindows) Unix.SetFilePermissions(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            File.Copy(ConfigPathOld, ConfigPath, true);
            if (!OSInfo.IsWindows) Unix.SetFilePermissions(ConfigPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            Directory.Delete(Path.GetDirectoryName(ConfigPathOld)!, true);
        }

        Configuration? config = null;
        if (File.Exists(ConfigPath))
        {
            // Read the configuration file if it exists.
            try
            {
                var txt = File.ReadAllText(ConfigPath);
                try
                {
                    config = JsonConvert.DeserializeObject<Configuration>(txt,
                        new Newtonsoft.Json.Converters.VersionConverter(), new StringEnumConverter());
                }
                catch (JsonException ex)
                {
#if Server
                    Logger?.LogError(ex, $"Error parsing {Path.GetFileName(ConfigPath)}; {ex.Message}");
#endif
                    return null;
                }
                if (config == null)
                {
                    return null;
                }

                if (config.Applications == null) config.Applications = new Applications();

                foreach (var app in config.Applications)
                {
                    app.IsSeparateFile = false;
                    if (app.Environment == null) app.Environment = new Dictionary<string, string>();
                }

                if (loadApps)
                {
                    bool hasError = false;
                    var separateConfigs = Directory.EnumerateFiles(Path.GetDirectoryName(ConfigPath)!, "*.json")
                        .Where(file => file != ConfigPath && file != ConfigPathOld)
                        .Select(file =>
                        {
                            var json = File.ReadAllText(file);
                            if (string.IsNullOrEmpty(json)) return null;
                            Application? app;
                            try
                            {
                                app = JsonConvert.DeserializeObject<Application>(json,
                                    new Newtonsoft.Json.Converters.VersionConverter(), new StringEnumConverter());
                            }
                            catch (JsonException ex)
                            {
#if Server
                                Logger?.LogError(ex, $"Error parsing {Path.GetFileName(file)}; {ex.Message}");
#else
                                throw;
#endif
                                return null;
                            }
                            if (app == null)
                            {
#if Server
                                Logger?.LogError($"Error parsing {Path.GetFileName(file)}.");
#else
                                throw new InvalidOperationException($"Error parsing {Path.GetFileName(file)}.");
#endif
                            }
                            if (app.Environment == null) app.Environment = new Dictionary<string, string>();
                            app.IsSeparateFile = true;
#if Server
                            var fileMode = Unix.GetFilePermissions(file);
                            var owner = Unix.GetOwnerAndGroup(file).Owner;
                            if ((owner != (app.User ?? config.User) ||
                                fileMode.HasFlag(UnixFileMode.GroupWrite) ||
                                fileMode.HasFlag(UnixFileMode.OtherWrite)))
                            {
                                // Retry loading app after one second, in case owner and mode of a freshly created file
                                // has not yet been set. (The file might have been created by Api's Save method).
                                if (!app.Offline && !app.IsReloading &&
                                    (DateTime.Now - File.GetCreationTime(file) < TimeSpan.FromSeconds(5)))
                                {
                                    app.IsReloading = true;
                                    Task.Run(async () =>
                                    {
                                        await Task.Delay(1000);
                                        app.Offline = false;
                                        await LoadAsync();
                                        app.IsReloading = false;
                                    });
                                }
                                app.Offline = true;
                                app.Status = Status.Error;
                                app.Error = $"The app json file {Path.GetFileName(file)} has a different owner than the app's User, or is writable by group or other. It must be writable only by it's owner, and must be owned by the same user specified in it's User json property.";
                                hasError = true;
                            }
#endif
                            return app;
                        })
                        .Where(app => app != null);
                    foreach (var sconfig in separateConfigs) config.Applications.Add(sconfig);
                    if (hasError) await config.SaveAsync(true, false);
                }
#if Server
                Logger?.LogInformation($"Loaded configuration file {ConfigPath}");
#endif
            }
            catch (Exception ex)
            {
#if Server
                Logger?.LogError(ex, $"Failed to read configuration file {ConfigPath}: {ex.Message}");
#endif
                throw;
            }
        }
        else
        {
            config = new Configuration()
            {
                Applications = new Applications(),
            };
        }
        return config;
    }

    internal Configuration? LoadConfig(bool loadApps = true)
    {
        // import old config path /etc/aspnetcore if it exists to /etc/aspnet-server
        if (File.Exists(ConfigPathOld))
        {
            var path = Path.GetDirectoryName(ConfigPath);
            if (path != null && !Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
                if (!OSInfo.IsWindows) Unix.SetFilePermissions(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            File.Copy(ConfigPathOld, ConfigPath, true);
            if (!OSInfo.IsWindows) Unix.SetFilePermissions(ConfigPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            Directory.Delete(Path.GetDirectoryName(ConfigPathOld)!, true);
        }

        Configuration? config = null;
        if (File.Exists(ConfigPath))
        {
            // Read the configuration file if it exists.
            try
            {
                var txt = File.ReadAllText(ConfigPath);
                try
                {
                    config = JsonConvert.DeserializeObject<Configuration>(txt,
                        new Newtonsoft.Json.Converters.VersionConverter(), new StringEnumConverter());
                }
                catch (JsonException ex)
                {
#if Server
                    Logger?.LogError(ex, $"Error parsing {Path.GetFileName(ConfigPath)}; {ex.Message}");
#endif
                    return null;
                }
                if (config == null)
                {
                    config = new Configuration();
                    config.Save(true, false);
                }

                if (config.Applications == null) config.Applications = new Applications();

                foreach (var app in config.Applications)
                {
                    app.IsSeparateFile = false;
                    if (app.Environment == null) app.Environment = new Dictionary<string, string>();
                }

                if (loadApps)
                {
                    bool hasError = false;
                    var separateConfigs = Directory.EnumerateFiles(Path.GetDirectoryName(ConfigPath), "*.json")
                        .Where(file => file != ConfigPath && file != ConfigPathOld)
                        .Select(file =>
                        {
                            var json = File.ReadAllText(file);
                            if (string.IsNullOrEmpty(json)) return null;
                            Application? app;
                            try
                            {
                                app = JsonConvert.DeserializeObject<Application>(json,
                                    new Newtonsoft.Json.Converters.VersionConverter(), new StringEnumConverter());
                            }
                            catch (JsonException ex)
                            {
#if Server
                                Logger?.LogError(ex, $"Error parsing {Path.GetFileName(file)}; {ex.Message}");
#else
                                throw;
#endif
                                return null;
                            }
                            if (app == null)
                            {
#if Server
                                Logger?.LogError($"Error parsing {Path.GetFileName(file)}.");
#else
                                throw new InvalidOperationException($"Error parsing {Path.GetFileName(file)}.");
#endif
                                return null;
                            }
                            if (app.Environment == null) app.Environment = new Dictionary<string, string>();
                            app.IsSeparateFile = true;
#if Server
                            var fileMode = Unix.GetFilePermissions(file);
                            var owner = Unix.GetOwnerAndGroup(file).Owner;
                            if ((owner != (app.User ?? config.User) ||
                                fileMode.HasFlag(UnixFileMode.GroupWrite) ||
                                fileMode.HasFlag(UnixFileMode.OtherWrite)))
                            {
                                // Retry loading app after one second, in case owner and mode of a freshly created file
                                // has not yet been set. (The file might have been created by Api's Save method).
                                if (!app.Offline && !app.IsReloading &&
                                    (DateTime.Now - File.GetCreationTime(file) < TimeSpan.FromSeconds(5)))
                                {
                                    app.IsReloading = true;
                                    Task.Run(async () =>
                                    {
                                        await Task.Delay(1000);
                                        app.Offline = false;
                                        Load();
                                        app.IsReloading = false;
                                    });
                                }
                                app.Offline = true;
                                app.Status = Status.Error;
                                app.Error = $"The app json file {Path.GetFileName(file)} has a different owner than the app's User, or is writable by group or other. It must be writable only by it's owner, and must be owned by the same user specified in it's User json property.";
                                hasError = true;
                            }
#endif
                            return app;
                        })
                        .Where(app => app != null);
                    foreach (var sconfig in separateConfigs) config.Applications.Add(sconfig);
                    if (hasError) config.Save(true, false);
                }
#if Server
                Logger?.LogInformation($"Loaded configuration file {ConfigPath}");
#endif
            }
            catch (Exception ex)
            {
#if Server
                Logger?.LogError(ex, $"Failed to read configuration file {ConfigPath}: {ex.Message}");
#endif
                throw;
            }
        }
        else
        {
            config = new Configuration()
            {
                Applications = new Applications(),
            };
        }
        return config;
    }

    public bool IsAppChanged(Application app, Application oapp, bool oldOffline)
    {
        return oapp.Assembly != app.Assembly || oapp.Urls != app.Urls || oapp.Arguments != app.Arguments ||
            oapp.ListenUrls != app.ListenUrls || oapp.User != app.User || oapp.Group != app.Group ||
            (oapp.Environment == null) != (app.Environment == null) ||
            oapp.Environment != null && app.Environment != null &&
           !oapp.Environment.Keys.All(key => app.Environment.ContainsKey(key) && app.Environment[key] == oapp.Environment[key]) ||
            oapp.IdleTimeout != app.IdleTimeout || oapp.Recycle != app.Recycle ||
            (oapp.Offline || oldOffline) != (app.Offline || Offline);
    }

    public async Task LoadAsync(bool useMutex = true)
    {
        var inside = Interlocked.Exchange(ref loadEntered, 1) == 1;
        if (inside || IsShuttingDown) return; // Prevent re-entrancy

        async Task LoadInternalAsync()
        {
            try
            {
                var config = await LoadConfigAsync();
                if (Applications == null) Applications = new Applications();
                IdleTimeout = config.IdleTimeout;
                Recycle = config.Recycle;
                EnableHttp3 = config.EnableHttp3;
                var ouser = User;
                User = config.User;
                Group = config.Group;
                var oldOffline = Offline;
                Offline = config.Offline;
                Syslog = config.Syslog;
                FailureInterval = config.FailureInterval;
                FailureLimit = config.FailureLimit;
                Command = Command.None;
                IdleTimeoutOnLowMemory = config.IdleTimeoutOnLowMemory;
                MemoryLowThreshold = config.MemoryLowThreshold;

#if Server
                if (config.Command == Command.Shutdown)
                {
                    // remove Shutdown command from config
                    config.Command = Command.None;
                    await config.SaveAsync(true, false);

                    // If the command is Shutdown, we should shutdown the server.
                    Logger?.LogInformation("Received shutdown command, shutting down server.");
                    await ShutdownAsync();

                    return;
                }
                else if (config.Command == Command.Restart)
                {
                    // remove restart command from config
                    config.Command = Command.None;
                    await config.SaveAsync(true, false);

                    // If the command is Restart, we should restart the server.
                    Logger?.LogInformation("Received restart command, restarting server.");
                    await RestartAsync();

                    return;
                }
#endif
                var oldApps = Applications
                    .OrderBy(app => app.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var newApps = (config?.Applications ?? new Applications())
                    .OrderBy(app => app.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
#if Server
                // If not runnig as root, forbid User & Group settings
                if (!OSInfo.IsWindows && !Unix.IsRoot)
                {
                    if (newApps.Any(app => !string.IsNullOrEmpty(app.User) || !string.IsNullOrEmpty(app.Group)))
                    {
                        Logger?.LogError("Cannot set User or Group of Application when not running AspNetCoreSharedServer as root.");
                        return;
                    }
                }
                // Ensure only root is allowed to modify configuration.json or there are only applications by owner. 
                if (!OSInfo.IsWindows)
                {
                    var owner = Unix.GetOwnerAndGroup(ConfigPath);
                    var mode = Unix.GetFilePermissions(ConfigPath);
                    if ((mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
                    {
                        Logger?.LogError("Only owner of /etc/aspnet-server/configuration.json must be allowed to modify it.");
                        return;
                    }
                    else
                    {
                        if (owner.Owner != "root")
                        {
                            foreach (var app in newApps)
                            {
                                if ((app.User ?? config?.User) != owner.Owner)
                                {
                                    await app.SetStatusAsync(Status.Error, $"Only {owner.Owner} allowed as User.", true, false);
                                }
                            }
                        }
                    }
                }
#endif
                int i = 0, oi = 0;
                for (i = 0; i < newApps.Count; i++)
                {
                    var app = newApps[i];
                    while (oi < oldApps.Count && string.Compare(oldApps[oi].Name, app.Name, true) < 0)
                    {
                        var oapp = oldApps[oi];
#if Server
                        if (oapp.Proxy != null)
                        {
                            await oapp.Proxy.ShutdownAsync(false);
                            oapp.Proxy = null;
                        }
#endif
                        oi++;
                    }
                    if (oi < oldApps.Count && oldApps[oi].Name.Equals(app.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        var oapp = oldApps[oi];
                        if (IsAppChanged(app, oapp, oldOffline))
                        {
#if Server
                            if (oapp.Proxy != null)
                            {
                                await oapp.Proxy.ShutdownAsync(false);
                                oapp.Proxy = null;
                            }
#endif
                            app.CopyTo(oapp);
#if Server
                            await ListenAsync(app);
#endif              
                        }
                        oi++;
                    }
                    else
                    {
                        Applications.Add(app);
#if Server
                        await ListenAsync(app);
#endif
                    }
                }

                while (oi < oldApps.Count)
                {
                    var oapp = oldApps[oi];
#if Server
                    if (oapp.Proxy != null)
                    {
                        await oapp.Proxy.ShutdownAsync(false);
                        oapp.Proxy = null;
                    }
#endif
                    Applications.Remove(oapp);
                    oi++;
                }

                if (!File.Exists(ConfigPath)) await SaveAsync(true, false);
                else await SaveIfDirtyAsync(true, false);
            }
            finally
            {
                Volatile.Write(ref loadEntered, 0);
            }
        }

        if (useMutex)
        {
            using (var mutex = await LockAsync()) await LoadInternalAsync();
        }
        else await LoadInternalAsync();
    }

    public void Load(bool useMutex = true)
    {
        var inside = Interlocked.Exchange(ref loadEntered, 1) == 1;
        if (inside || IsShuttingDown) return; // Prevent re-entrancy

        void LoadInternal()
        {
            try
            {
                var config = LoadConfig();
#if Server
                if (config.Command == Command.Shutdown)
                {
                    // remove Shutdown command from config
                    config.Command = Command.None;
                    config.Save(true, false);

                    // If the command is Shutdown, we should shutdown the server.
                    Logger?.LogInformation("Received shutdown command, shutting down server.");
                    Shutdown();

                    return;
                }
                else if (config.Command == Command.Restart)
                {
                    // remove restart command from config
                    config.Command = Command.None;
                    config.Save(true, false);

                    // If the command is Restart, we should restart the server.
                    Logger?.LogInformation("Received restart command, restarting server.");
                    Restart();

                    return;
                }
#endif
                if (Applications == null) Applications = new Applications();

                IdleTimeout = config.IdleTimeout;
                Recycle = config.Recycle;
                EnableHttp3 = config.EnableHttp3;
                var ouser = User;
                User = config.User;
                Group = config.Group;
                var oldOffline = Offline;
                Offline = config.Offline;
                Syslog = config.Syslog;
                FailureInterval = config.FailureInterval;
                FailureLimit = config.FailureLimit;
                Command = Command.None;
                IdleTimeoutOnLowMemory = config.IdleTimeoutOnLowMemory;
                MemoryLowThreshold = config.MemoryLowThreshold;

                var oldApps = Applications
                    .OrderBy(app => app.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var newApps = (config?.Applications ?? new Applications())
                    .OrderBy(app => app.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

#if Server
                // If not runnig as root, forbid User & Group settings
                if (!OSInfo.IsWindows && !Unix.IsRoot)
                {
                    if (newApps.Any(app => !string.IsNullOrEmpty(app.User) || !string.IsNullOrEmpty(app.Group)))
                    {
                        Logger?.LogError("Cannot set User or Group of Application when not running AspNetCoreSharedServer as root.");
                        return;
                    }
                }
                // Ensure only root is allowed to modify configuration.json or there are only applications by owner. 
                if (!OSInfo.IsWindows)
                {
                    var owner = Unix.GetOwnerAndGroup(ConfigPath);
                    var mode = Unix.GetFilePermissions(ConfigPath);
                    if ((mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
                    {
                        Logger?.LogError("Only owner of /etc/aspnet-server/configuration.json must be allowed to modify it.");
                        return;
                    }
                    else
                    {
                        if (owner.Owner != "root")
                        {
                            foreach (var app in newApps)
                            {
                                if ((app.User ?? config?.User) != owner.Owner)
                                {
                                    app.SetStatus(Status.Error, $"Only {owner.Owner} allowed as User.", true, false);
                                }
                            }
                        }
                    }
                }
#endif
                int i = 0, oi = 0;
                for (i = 0; i < newApps.Count; i++)
                {
                    var app = newApps[i];
                    while (oi < oldApps.Count && string.Compare(oldApps[oi].Name, app.Name, true) < 0)
                    {
                        var oapp = oldApps[oi];
#if Server

                        if (oapp.Proxy != null)
                        {
                            oapp.Proxy.Shutdown(false);
                            oapp.Proxy = null;
                        }
#endif
                        oi++;
                    }
                    if (oi < oldApps.Count && oldApps[oi].Name.Equals(app.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        var oapp = oldApps[oi];
                        if (IsAppChanged(app, oapp, oldOffline))
                        {
#if Server
                            if (oapp.Proxy != null)
                            {
                                oapp.Proxy.Shutdown(false);
                                oapp.Proxy = null;
                            }
#endif
                            app.CopyTo(oapp);
#if Server
                            Listen(app);
#endif
                        }
                        oi++;
                    }
                    else
                    {
                        Applications.Add(app);
                        Listen(app);
                    }
                }

                while (oi < oldApps.Count)
                {
                    var oapp = oldApps[oi];
#if Server
                    throw new NotImplementedException();

                    /*if (oapp.Proxy != null)
                    {
                        oapp.Proxy.Shutdown();
                        oapp.Proxy = null;
                    }*/
#endif
                    Applications.Remove(oapp);
                    oi++;
                }

                if (!File.Exists(ConfigPath)) Save(true, false);
                else SaveIfDirty(true, false);
            }
            finally
            {
                Volatile.Write(ref loadEntered, 0);
            }
        }

        if (useMutex)
        {
            using (var mutex = Lock()) LoadInternal();
        }
        else LoadInternal();
    }

    public void Listen(Application app)
    {
#if Server
        if (!IsShuttingDown && !app.Offline && !Offline)
        {
            var server = new Proxy(app);
            var listener = Task.Run(async () =>
            {
                try
                {
                    await server.ListenAsync();
                    await app.SetStatusAsync(Status.Stopped, null, false, false);
                }
                catch (Exception ex)
                {
                }
            });
            app.SetStatus(Status.Running, null, false, false);
        }
#endif
    }
    public async Task ListenAsync(Application app)
    {
#if Server
        if (!IsShuttingDown && !app.Offline && !Offline)
        {
            var server = new Proxy(app);
            var listener = Task.Run(async () =>
            {
                try
                {
                    await server.ListenAsync();
                    await app.SetStatusAsync(Status.Stopped, null, false, false);
                }
                catch (Exception ex)
                {
                }
            });
            await app.SetStatusAsync(Status.Running, null, false, false);
        }
#endif
    }

    public void SaveInternal(bool disableWatcher)
    {
        var dir = Path.GetDirectoryName(ConfigPath);
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
            if (AllowOnlyRootToCreateApplications)
            {
                Unix.SetFilePermissions(dir,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            else
            {
                Unix.SetFilePermissions(dir,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute);
            }
        }

        if (Applications == null) Applications = new Applications();
        var oapps = Applications;
        var extapps = oapps.Where(app => app.IsSeparateFile).ToList();
        foreach (var extapp in extapps) Applications.Remove(extapp);
        foreach (var app in Applications)
        {
            if (app.Environment == null || app.Environment.Count == 0) app.Environment = null;
        }
        if (oapps.Count == 0) Applications = null;
        var json = JsonConvert.SerializeObject(this, Formatting.Indented,
            new JsonSerializerSettings { DefaultValueHandling = DefaultValueHandling.Ignore });
        if (Applications == null) Applications = new Applications();
        foreach (var app in Applications)
        {
            if (app.Environment == null) app.Environment = new Dictionary<string, string>();
        }
#if Server
        if (watcher != null && disableWatcher) watcher.EnableRaisingEvents = false;
#endif
        File.WriteAllText(ConfigPath, json);
        foreach (var extapp in extapps)
        {
            Applications.Add(extapp); // re-add app to Applications
            if (extapp.Environment == null || extapp.Environment.Count == 0) extapp.Environment = null;
            var extjson = JsonConvert.SerializeObject(extapp, Formatting.Indented,
                new JsonSerializerSettings { DefaultValueHandling = DefaultValueHandling.Ignore });
            if (extapp.Environment == null) extapp.Environment = new Dictionary<string, string>();
            var file = Path.Combine(Path.GetDirectoryName(ConfigPath)!, $"{extapp.Name}.json");
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(ConfigPath)!, file), extjson);
            try
            {
                if (!string.IsNullOrEmpty(extapp.User ?? User)) Unix.SetOwnerAndGroup(file, extapp.User ?? User!, WwwData);
            }
            catch { }
            Unix.SetFilePermissions(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        // If runnig as root, set permissions to read/write for root only
        if (!OSInfo.IsWindows /* && Unix.getuid() == 0 */)
        {
            Unix.SetFilePermissions(Path.GetDirectoryName(ConfigPath)!, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Unix.SetFilePermissions(ConfigPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        foreach (var app in Applications) app.Dirty = false;
    }
    public async Task SaveAsync(bool disableWatcher = false, bool useMutex = true)
    {
        if (useMutex)
        {
            using (var mutex = await LockAsync())
            {
                SaveInternal(disableWatcher);
#if Server
                await Task.Delay(100);
                if (watcher != null && disableWatcher) watcher.EnableRaisingEvents = true;
#endif
            }
        }
        else
        {
            SaveInternal(disableWatcher);
#if Server
            await Task.Delay(100);
            if (watcher != null && disableWatcher) watcher.EnableRaisingEvents = true;
#endif

        }
    }

    public void Save(bool disableWatcher = false, bool useMutex = true)
    {
        if (useMutex)
        {
            using (var mutex = Lock())
            {
                SaveInternal(disableWatcher);
#if Server
                Thread.Sleep(100);
                if (watcher != null && disableWatcher) watcher.EnableRaisingEvents = true;
#endif
            }
        }
        else
        {
            SaveInternal(disableWatcher);
#if Server
            Thread.Sleep(100);
            if (watcher != null && disableWatcher) watcher.EnableRaisingEvents = true;
#endif
        }
    }

    public async Task SaveIfDirtyAsync(bool disableWatcher = false, bool useMutex = true)
    {
        if (Applications != null && Applications.Any(app => app.Dirty))
        {
            await SaveAsync(disableWatcher, useMutex);
        }
    }
    public void SaveIfDirty(bool disableWatcher = false, bool useMutex = true)
    {
        if (Applications != null && Applications.Any(app => app.Dirty))
        {
            Save(disableWatcher, useMutex);
        }
    }
    public void Add(Application app)
    {
        using (var mutex = Lock())
        {
            Load(false);
            var oapp = Applications?[app.Name];
            if (oapp != null)
            {
                if (IsAppChanged(app, oapp, Offline))
                {
#if Server
                    if (oapp.Proxy != null)
                    {
                        oapp.Proxy.Shutdown(false);
                        oapp.Proxy = null;
                    }
#endif
                    Applications!.Remove(oapp);
                    Applications.Add(app);
                    Listen(app);
                }
                else
                {
#if Server
                    app.Proxy = oapp.Proxy;
#endif
                    Applications!.Remove(oapp);
                    Applications.Add(app);
                    return; // No changes, nothing to do.
                }
            }
            else
            {
                if (Applications == null) Applications = new Applications();
                Applications.Add(app);
                Listen(app);
            }
            Save(true, false);
        }
    }
    public async Task AddAsync(Application app)
    {
        using (var mutex = await LockAsync())
        {
            await LoadAsync(false);
            var oapp = Applications?[app.Name];
            if (oapp != null)
            {
                if (IsAppChanged(app, oapp, Offline))
                {
#if Server
                    if (oapp.Proxy != null)
                    {
                        await oapp.Proxy.ShutdownAsync(false);
                        oapp.Proxy = null;
                    }
#endif
                    Applications!.Remove(oapp);
                    Applications.Add(app);
                    await ListenAsync(app);
                }
                else
                {
#if Server
                    app.Proxy = oapp.Proxy;
#endif
                    Applications!.Remove(oapp);
                    Applications.Add(app);
                    return; // No changes, nothing to do.
                }
            }
            else
            {
                if (Applications == null) Applications = new Applications();
                Applications.Add(app);
                await ListenAsync(app);
            }
            await SaveAsync(true, false);
        }
    }

    public async Task RemoveAsync(string name)
    {
        using (var mutex = await LockAsync())
        {
            await LoadAsync(false);
            var app = Applications?[name];
            if (app != null)
            {
#if Server
                if (app.Proxy != null)
                {
                    await app.Proxy.ShutdownAsync();
                    app.Proxy = null;
                }
#endif
                Applications!.Remove(app);
                await SaveAsync(true, false);
            }
        }
    }
    public void Remove(string name)
    {
        using (var mutex = Lock())
        {
            Load(false);
            var app = Applications?[name];
            if (app != null)
            {
#if Server
                if (app.Proxy != null)
                {
                    app.Proxy.Shutdown();
                    app.Proxy = null;
                }
#endif
                Applications!.Remove(app);
                Save(true, false);
            }
        }
    }

    public void Update(Application app) => Add(app);
    public async Task UpdateAsync(Application app) => await AddAsync(app);
    public void Remove(Application app) => Remove(app.Name);
    public async Task RemoveAsync(Application app) => await RemoveAsync(app.Name);

    static Configuration configuration = null;
    public static Configuration Current => configuration ??= new Configuration();

    static Configuration() { }

    static FileSystemWatcher? watcher = null;
    CancellationTokenSource? reloadCts = new CancellationTokenSource();

    public void Watch()
    {
        var dir = Path.GetDirectoryName(ConfigPath);
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

        watcher = new FileSystemWatcher(dir, "*.json");
        watcher.Changed += (_, __) =>
        {
            reloadCts?.Cancel();

            reloadCts = new CancellationTokenSource();

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(500, reloadCts.Token);
                    if (!reloadCts.IsCancellationRequested) Load();
                }
                catch (TaskCanceledException)
                {
                }
            });
        };
        watcher.EnableRaisingEvents = true;
        Load();
    }
    public async Task WatchAsync()
    {
        var dir = Path.GetDirectoryName(ConfigPath);
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

        watcher = new FileSystemWatcher(dir, "*.json");
        watcher.Changed += (_, __) =>
        {
            reloadCts?.Cancel();

            reloadCts = new CancellationTokenSource();

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(500, reloadCts.Token);
                    if (!reloadCts.IsCancellationRequested) await LoadAsync();
                }
                catch (TaskCanceledException)
                {
                }
            });
        };
        watcher.EnableRaisingEvents = true;

        await LoadAsync();
    }

    public void Shutdown()
    {
#if Server
        if (watcher != null)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
            watcher = null;
        }
        IEnumerable<Application> apps;
        using (var mutex = Lock())
        {
            IsShuttingDown = true;
            var config = LoadConfig();
            apps = Applications?.ToList() ?? Enumerable.Empty<Application>();
            foreach (var app in apps)
            {
                app.Status = Status.Stopped;
            }
            config.Save(false, false);
        }
        var tasks = apps
            .Where(app => app.Proxy != null)
            .Select(async app =>
            {
                return Task.Run(() =>
                {
                    app.Proxy.Shutdown();
                    app.Proxy = null;
                });
            });
        Task.WaitAll(tasks);
#else
        using var mutex = Lock();
        Command = Command.Shutdown;
        Save(true, false);
#endif
    }

    public async Task ShutdownAsync()
    {
#if Server
        if (watcher != null)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
            watcher = null;
        }
        IEnumerable<Application> apps;
        using (var mutex = await LockAsync())
        {
            IsShuttingDown = true;
            var config = await LoadConfigAsync();
            apps = config?.Applications?.ToList() ?? Enumerable.Empty<Application>();
            foreach (var app in apps)
            {
                app.Status = Status.Stopped;
            }
            await (config?.SaveAsync(false, false) ?? Task.CompletedTask);
        }
        var tasks = apps
            .Where(app => app.Proxy != null)
            .Select(async app =>
            {
                return Task.Run(async () =>
                {
                    await (app.Proxy?.ShutdownAsync() ?? Task.CompletedTask);
                    app.Proxy = null;
                });
            });
        Task.WaitAll(tasks);
#else
        using var mutex = await LockAsync();
        Command = Command.Shutdown;
        await SaveAsync(true, false);
#endif
    }
    public void Restart()
    {
#if Server
        IEnumerable<Application> apps;
        using (var mutex = Lock())
        {
            apps = Applications?.ToList() ?? Enumerable.Empty<Application>();
        }
        var tasks = apps
            .Where(app => app.Proxy != null)
            .Select(async app =>
            {
                return Task.Run(() =>
                {
                    app.Proxy.Shutdown(false);
                    app.Proxy = null;
                });
            });
        Task.WaitAll(tasks);
        Load();
#else
        using var mutex = Lock();
        Command = Command.Restart;
        Save(true, false);
#endif
    }
    public async Task RestartAsync()
    {
#if Server
        IEnumerable<Application> apps;
        using (var mutex = await LockAsync())
        {
            apps = Applications?.ToList() ?? Enumerable.Empty<Application>();
            var tasks = apps
                .Where(app => app.Proxy != null)
                .Select(async app =>
                {
                    return Task.Run(async () =>
                    {
                        await app.Proxy.ShutdownAsync(false);
                        app.Proxy = null;
                    });
                });
            await Task.WhenAll(tasks);
        }
        await LoadAsync();
#else
        using var mutex = await LockAsync();
        Command = Command.Restart;
        Save(true, false);
#endif
    }
}
public class Applications : KeyedCollection<string, Application>
{
    public Applications() : base(StringComparer.OrdinalIgnoreCase) { }
    protected override string GetKeyForItem(Application item)
    {
        if (item.Name == "configuration") throw new NotSupportedException("Application name \"configuration\" is reserved.");
        if (item.Name == "applications") throw new NotSupportedException("Application name \"applications\" is reserved.");
        return item.Name;
    }
    protected override void InsertItem(int index, Application item)
    {
        if (item.Name == "configuration") throw new NotSupportedException("Application name \"configuration\" is reserved.");
        if (item.Name == "applications") throw new NotSupportedException("Application name \"applications\" is reserved.");
        base.InsertItem(index, item);
    }
    protected override void SetItem(int index, Application item)
    {
        if (item.Name == "configuration") throw new NotSupportedException("Application name \"configuration\" is reserved.");
        if (item.Name == "applications") throw new NotSupportedException("Application name \"applications\" is reserved.");
        base.SetItem(index, item);
    }
    public new Application this[string name]
    {
        get
        {
            if (Contains(name)) return base[name];
            else return null;
        }
    }
}
public class Application
{
    public string Name { get; set; } = string.Empty;
    [JsonIgnore]
    string listenUrls = string.Empty;
    [JsonIgnore]
    public bool Dirty = false;
    public string ListenUrls
    {
        get => listenUrls;
        set
        {
            Dirty = !string.IsNullOrEmpty(listenUrls) && listenUrls != value;
            listenUrls = value;
        }
    }
    [DefaultValue("")]
    public string Urls { get; set; } = string.Empty;
    [DefaultValue(null)]
    public Dictionary<string, string>? Environment { get; set; } = new Dictionary<string, string>();
    public string Assembly { get; set; } = string.Empty;
    [DefaultValue(null)]
    public string? WorkingDirectory {  get; set; } = null;
    [DefaultValue(null)]
    public string? Path { get; set; } = null;
    [DefaultValue("")]
    public string Arguments { get; set; } = string.Empty;
    [DefaultValue(null)]
    public bool? EnableHttp3 { get; set; } = null;
    [DefaultValue(null)]
    public TimeSpan? IdleTimeout { get; set; } = null;
    [DefaultValue(null)]
    public TimeSpan? Recycle { get; set; } = null;
    [DefaultValue(null)]
    public string? User { get; set; } = null;
    [DefaultValue(null)]
    public string? Group { get; set; } = null;
    [DefaultValue(false)]
    public bool Offline { get; set; } = false;
    [DefaultValue(Status.Stopped)]
    public Status Status { get; set; } = Status.Stopped;
    [DefaultValue(null)]
    public string? Error { get; set; } = null;
    [DefaultValue(Command.None)]
    public Command Command { get; set; } = Command.None;
#if Server
    [JsonIgnore]
    public Proxy? Proxy { get; set; } = null;
#endif
    [JsonIgnore]
    public bool IsSeparateFile { get; set; } = true;
    internal bool IsReloading = false;
    public void CopyTo(Application app)
    {
        app.Arguments = Arguments;
        app.Assembly = Assembly;
        app.Command = Command;
        app.Dirty = Dirty;
        app.Offline = Offline;
        app.EnableHttp3 = EnableHttp3;
        app.Environment = Environment;
        app.Error = Error;
        app.Group = Group;
        app.IdleTimeout = IdleTimeout;
        app.ListenUrls = ListenUrls;
        app.Name = Name;
        app.Path = Path;
        app.Recycle = Recycle;
        app.Status = Status;
        app.Urls = Urls;
        app.User = User;
        app.IsSeparateFile = IsSeparateFile;
#if Server
        app.Proxy = Proxy;
#endif
    }

    internal void SetStatus(Status status, string? error, bool disable = false, bool load = true)
    {
        using var mutex = AspServer.Lock();
        if (load) Configuration.Current.Load(false);
        var config = Configuration.Current;
        var app = config.Applications[Name];
        if (app != null && (app.Status != status || app.Error != error))
        {
            bool disableChanged = false;
            app.Status = status; app.Error = error;
            if (disable)
            {
                disableChanged = !app.Offline;
                app.Offline = true;
            }
            config.Save(true, false);
            if (disableChanged && load) _ = Task.Run(() => Configuration.Current.Load(false));
        }
    }
    internal async Task SetStatusAsync(Status status, string? error, bool disable = false, bool load = true)
    {
        using var mutex = await AspServer.LockAsync();
        if (load) await Configuration.Current.LoadAsync(false);
        var config = Configuration.Current;
        var app = config.Applications?[Name];
        if (app != null && (app.Status != status || app.Error != error))
        {
            bool disableChanged = false;
            app.Status = status; app.Error = error;
            if (disable)
            {
                disableChanged = !app.Offline;
                app.Offline = true;
            }
            await config.SaveAsync(true, false);
            if (disableChanged && load) _ = Task.Run(async () => Configuration.Current.LoadAsync(false));
        }
    }

    public static IEnumerable<string> FindAssemblies(string path)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) yield break;

        var webConfig = System.IO.Path.Combine(path, "web.config");
        if (!File.Exists(webConfig)) webConfig = System.IO.Path.Combine(path, "Web.config");
        if (File.Exists(webConfig))
        {
            // If web.config exists, it's likely an ASP.NET application
            var xml = System.Xml.Linq.XDocument.Load(webConfig);
            var webServer = xml.Descendants("system.webServer").FirstOrDefault();
            var aspNetCore = webServer?.Element("aspNetCore");
            if (aspNetCore != null)
            {
                var process = aspNetCore.Attribute("processPath")?.Value;
                if (process == "dotnet")
                {
                    var arguments = aspNetCore.Attribute("arguments")?.Value;
                    if (!string.IsNullOrEmpty(arguments))
                    {
                        yield return System.IO.Path.Combine(path, arguments);
                    }
                }
                else if (!string.IsNullOrEmpty(process)) yield return System.IO.Path.Combine(path, process);
            }
            yield break;
        }

        foreach (var file in Directory.GetFiles(path, "*.*", SearchOption.TopDirectoryOnly))
        {
            if (file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                AssemblyDefinition assembly = null;
                try
                {
                    assembly = AssemblyDefinition.ReadAssembly(file);
                }
                catch { }
                var targetFramework = assembly?.CustomAttributes
                    .FirstOrDefault(attr => attr.AttributeType.FullName == "System.Runtime.Versioning.TargetFrameworkAttribute")
                    ?.ConstructorArguments.FirstOrDefault().Value as string;
                if (targetFramework != null && targetFramework.IndexOf("Framework", StringComparison.OrdinalIgnoreCase) < 0 &&
                    assembly!.EntryPoint != null &&
                    assembly.Modules.Any(mod => mod.GetTypeReferences().Any(t => t.FullName.StartsWith("Microsoft.AspNetCore."))))
                    yield return file;
            }
            else if (OSInfo.IsWindows)
            {
                if (file.EndsWith("*.exe", StringComparison.OrdinalIgnoreCase) && !File.Exists(System.IO.Path.ChangeExtension(file, ".dll")))
                    yield return file;
            }
            else if (OSInfo.IsLinux)
            {
                var bytes = new byte[4];
                using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read))
                {
                    stream.Read(bytes, 0, 4);
                    // Check for ELF magic number
                    if (bytes[0] == 0x7F && bytes[1] == 0x45 && bytes[2] == 0x4C && bytes[3] == 0x46)
                    //|| bytes[0] == 0x23 && bytes[1] == 0x21) // #! shebang
                    {
                        yield return file;
                    }
                }
            }
            else if (OSInfo.IsMac)
            {
                var bytes = new byte[4];
                using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read))
                {
                    stream.Read(bytes, 0, 4);
                    // Is Mac executable (AOT)
                    if (bytes[0] == 0xFE && bytes[1] == 0xED && bytes[2] == 0xFA && bytes[3] == 0xCE ||
                        bytes[0] == 0xCE && bytes[1] == 0xFA && bytes[2] == 0xED && bytes[3] == 0xFE ||
                        bytes[0] == 0xCF && bytes[1] == 0xFA && bytes[2] == 0xED && bytes[3] == 0xFE)
                    {
                        yield return file;
                    }
                }
            }
        }
    }
}

public static class AspServer
{
    public static Configuration Configuration => Configuration.Current;

    static void SetOffline(string appId, bool disabled)
    {
        if (appId == null)
        {
            using var mutex = Configuration.Lock();
            var config = Configuration.LoadConfig();
            if (config != null && config.Offline != disabled)
            {
                config.Offline = disabled;
                config.Save(true, false);
                Configuration.Load(false);
            }
        }
        else
        {
            using var mutex = Configuration.Lock();
            var config = Configuration.LoadConfig();
            if (config != null && config.Applications != null && config.Applications[appId].Offline != disabled)
            {
                config.Applications[appId].Offline = disabled;
                config.Save(true, false);
                Configuration.Load(false);
            }
        }
    }
    static async Task SetOfflineAsync(string appId, bool disabled)
    {
        if (appId == null)
        {
            using var mutex = await Configuration.LockAsync();
            var config = await Configuration.LoadConfigAsync();
            if (config != null && config.Offline != disabled)
            {
                config.Offline = disabled;
                await config.SaveAsync(true, false);
                await Configuration.LoadAsync(false);
            }
        }
        else
        {
            using var mutex = await Configuration.LockAsync();
            var config = await Configuration.LoadConfigAsync();
            if (config != null && config.Applications != null && config.Applications[appId].Offline != disabled)
            {
                config.Applications[appId].Offline = disabled;
                await config.SaveAsync(true, false);
                await Configuration.LoadAsync(false);
            }
        }
    }

    public static void Start(string appId = null) => SetOffline(appId, false);
    public static void Stop(string appId = null) => SetOffline(appId, true);
    public static void Restart(string appId = null)
    {
        Stop(appId);
        Start(appId);
    }
    public static async Task StartAsync(string appId = null) => await SetOfflineAsync(appId, false);
    public static async Task StopAsync(string appId = null) => await SetOfflineAsync(appId, true);
    public static async Task RestartAsync(string appId = null)
    {
        await StopAsync(appId);
        await StartAsync(appId);
    }

    public static Status Status(string appId = null)
    {
        using var mutex = Configuration.Lock();
        if (appId == null)
        {
            return Configuration.Offline ? AspNetCoreSharedServer.Status.Running : AspNetCoreSharedServer.Status.Stopped;
        }
        else
        {
            return Configuration.Applications?[appId].Status ??
                throw new KeyNotFoundException($"There is no application {appId}");
        }
    }
    public static async Task<Status> StatusAsync(string appId = null)
    {
        using var mutex = await Configuration.LockAsync();
        if (appId == null)
        {
            return Configuration.Offline ? AspNetCoreSharedServer.Status.Running : AspNetCoreSharedServer.Status.Stopped;
        }
        else
        {
            return Configuration.Applications?[appId].Status ??
                throw new KeyNotFoundException($"There is no application {appId}");
        }
    }

    public static void Shutdown() => Configuration.Shutdown();
    public static async Task ShutdownAsync() => await Configuration.ShutdownAsync();
    public static int FindFreePort() => Configuration.FindFreePort();
    public static async Task<int> FindFreePortAsync() => await Configuration.FindFreePortAsync();
    public static IDisposable Lock() => Configuration.Lock();
    public static Task<IDisposable> LockAsync() => Configuration.LockAsync();

}