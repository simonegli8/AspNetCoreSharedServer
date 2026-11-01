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
    public const bool AllowOnlyAdminsToCreateApplications = true;
    public const bool AllowOnlyRootToCreateApplications = !AllowOnlyAdminsToCreateApplications;
    public const string AdminGroup = "aspnet-server";
    public const string WwwData = "www-data";
    public static readonly TimeSpan LockTimeout = TimeSpan.FromMinutes(2);
    public static readonly string LockName = $"EstrellasDeEsperanza-aspnet-server";

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

    /// <summary>True if the UDP port is free on IPv4 and, where the OS supports it, on IPv6.</summary>
    static bool IsUdpPortFree(int port)
    {
        foreach (var family in new[] { AddressFamily.InterNetwork, AddressFamily.InterNetworkV6 })
        {
            if (family == AddressFamily.InterNetworkV6 && !Socket.OSSupportsIPv6) continue;
            try
            {
                using var probe = new Socket(family, SocketType.Dgram, ProtocolType.Udp);
                probe.Bind(new IPEndPoint(family == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any, port));
            }
            catch (SocketException)
            {
                return false;
            }
        }
        return true;
    }

    /// <param name="temporary">Pick any free port from the OS, without reserving it in the configuration.</param>
    /// <param name="alsoUdp">The same port number must also be free for UDP. Kestrel uses one number for TCP and for QUIC (HTTP/3).</param>
    public static async Task<int> FindFreePortAsync(bool temporary = false, bool alsoUdp = false)
    {
        if (temporary && alsoUdp)
        {
            for (int attempt = 0; attempt < 20; attempt++)
            {
                using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                probe.Bind(new IPEndPoint(IPAddress.Any, 0));
                var candidate = ((IPEndPoint)probe.LocalEndPoint!).Port;
                if (IsUdpPortFree(candidate)) return candidate;
            }
            // No such port found: continue with a TCP-only port. The app then works, but without HTTP/3 for this run.
        }
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
                var apps = Configuration.Current.Applications ?? new Applications();
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
            localEP = (IPEndPoint)socket.LocalEndPoint!;
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
                var apps = Configuration.Current.Applications ?? new Applications();
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
            localEP = (IPEndPoint)socket.LocalEndPoint!;
            port = localEP.Port;
        }
        finally
        {
            socket.Close();
        }
        return port;
    }

    /// <summary>
    /// The command line option that moves the configuration folder, which is <c>/etc/aspnet-server</c> on Linux and macOS and
    /// <c>%ProgramData%\aspnet-server</c> on Windows: <c>aspnet-server --config-dir &lt;folder&gt;</c>, or <c>--config-dir=&lt;folder&gt;</c>. The
    /// tests use it, so that they never touch the real configuration. It is deliberately not an environment variable: the environment of
    /// a process that runs as root or SYSTEM is handed down by whatever started it (sudo, a service manager, a shell profile), while a
    /// command line is what the person who starts the server writes. A service never has it, so the service always uses the fixed
    /// folder. The folder is as protected as the default one.
    /// </summary>
    public const string ConfigDirectoryOption = "--config-dir";

    /// <summary>The folder given with <see cref="ConfigDirectoryOption"/>, or null for the fixed one. Set once at startup, by <see cref="TakeConfigDirectory"/>.</summary>
    internal static string? ConfigDirectoryOverride { get; set; }

    /// <summary>
    /// Takes <see cref="ConfigDirectoryOption"/> and its folder out of the command line arguments, sets
    /// <see cref="ConfigDirectoryOverride"/> from it (as a full path) and returns the other arguments, in their order. Without the option
    /// nothing changes. Throws <see cref="ArgumentException"/> if the option has no folder.
    /// </summary>
    internal static string[] TakeConfigDirectory(string[] args)
    {
        var rest = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            string? directory = null;
            if (args[i].Equals(ConfigDirectoryOption, StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length || args[i + 1].Length == 0) throw new ArgumentException($"{ConfigDirectoryOption} needs a folder.");
                directory = args[++i];
            }
            else if (args[i].StartsWith(ConfigDirectoryOption + "=", StringComparison.OrdinalIgnoreCase))
            {
                directory = args[i].Substring(ConfigDirectoryOption.Length + 1);
                if (directory.Length == 0) throw new ArgumentException($"{ConfigDirectoryOption} needs a folder.");
            }

            if (directory == null) rest.Add(args[i]);
            else ConfigDirectoryOverride = Path.GetFullPath(directory);
        }
        return rest.ToArray();
    }

    internal static string ConfigDirectory =>
        ConfigDirectoryOverride ?? (OSInfo.IsWindows ?
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "aspnet-server") :
            "/etc/aspnet-server");

    [JsonIgnore]
    public string ConfigPathOld => ConfigDirectoryOverride is { } directory ? Path.Combine(directory, "applications.json") :
        OSInfo.IsWindows ? Path.Combine(ConfigDirectory, "applications.json") :
        "/etc/aspnetcore/applications.json";
    [JsonIgnore]
    public string ConfigPath => Path.Combine(ConfigDirectory, "configuration.json");

    /// <summary>
    /// True if there is a configuration of the old version to import, which lives in a folder of its own (<c>/etc/aspnetcore</c>). The import
    /// deletes that folder, so it never happens when the old file is in the folder of the current configuration, as with
    /// <see cref="ConfigDirectoryOption"/> and on Windows.
    /// </summary>
    internal bool HasOldConfig =>
        Path.GetDirectoryName(ConfigPathOld) != Path.GetDirectoryName(ConfigPath) && File.Exists(ConfigPathOld);

#if Server
    [JsonIgnore]
    public ILogger<Worker>? Logger { get; set; } = null;
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
    string? runningUser = null;
    bool runningUserSet = false;
    [JsonIgnore]
    public string? RunningUser {
        get => runningUserSet ? runningUser : User;
        set {
            runningUser = value;
            runningUserSet = true;
        }
    }
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
    [DefaultValue(null)]
    public string? Error { get; set; } = null;
    int loadEntered = 0;

    static AsyncMutexLock? @lock = null;
    static string? lockPath = null;
    static readonly object lockCreation = new object();
    /// <summary>
    /// The machine wide lock of the configuration. Its file is in the (protected) configuration folder, see <see cref="LockFiles"/>, so
    /// the lock follows the folder: it is made again when the folder changes, which only the tests do.
    /// </summary>
    public static AsyncMutexLock MutexLock
    {
        get
        {
            lock (lockCreation)
            {
                var path = LockFiles.PathFor(LockName);
                if (@lock == null || lockPath != path)
                {
                    @lock = new AsyncMutexLock(path);
                    lockPath = path;
                }
                return @lock;
            }
        }
    }
    public static Task<IDisposable> LockAsync(CancellationToken cancel = default) =>
        MutexLock.LockAsync(CancellationTokenSource.CreateLinkedTokenSource(cancel,
            new CancellationTokenSource(LockTimeout).Token).Token);
    public static IDisposable Lock(CancellationToken cancel = default) =>
        MutexLock.Lock(CancellationTokenSource.CreateLinkedTokenSource(cancel,
            new CancellationTokenSource(LockTimeout).Token).Token);
    /// <summary>The error that is shown when <see cref="SecureFile.IsInsecure"/> is true for the configuration.json.</summary>
    string ConfigFileSecurityError(string? user, SecureFile file) => OSInfo.IsWindows || file.IsLink ?
        $"For security reasons, the configuration.json must be changeable only by administrators{(user != null ? $" and {user}" : "")}" +
            $"{(file.Problem(user) is { } problem ? $" ({problem})" : "")}." :
        $"For security reasons, the configuration.json must be owned by root {(user != null ? $"or {user} " : "")}and must not have group & other write permission set.";

    internal async Task<Configuration?> LoadConfigAsync(bool loadApps = true)
    {
        // import old config path /etc/aspnetcore if it exists to /etc/aspnet-server
        if (HasOldConfig)
        {
            var path = Path.GetDirectoryName(ConfigPath);
            if (path != null && !Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
                if (!OSInfo.IsWindows) Unix.SetFilePermissions(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                else if (WindowsFileSecurity.IsAdministrator()) WindowsFileSecurity.ProtectDirectory(path, AdminGroup);
            }
            File.Copy(ConfigPathOld, ConfigPath, true);
            if (!OSInfo.IsWindows) Unix.SetFilePermissions(ConfigPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            else if (WindowsFileSecurity.IsAdministrator()) WindowsFileSecurity.Protect(ConfigPath, null);

            Directory.Delete(Path.GetDirectoryName(ConfigPathOld)!, true);
        }

        Configuration? config = null;
        if (File.Exists(ConfigPath))
        {
            // Read the configuration file if it exists.
            try
            {
                // The owner and permissions are checked on the handle that the text is read from, not on the path.
                var secure = SecureFile.Read(ConfigPath);
                if (secure.IsLink)
                {
#if Server
                    Logger?.LogError($"For security reasons, {Path.GetFileName(ConfigPath)} must not be a symbolic link. It is not loaded.");
#endif
                    return null; // not written back either: that would write through the link
                }
                {
                    if (secure.IsInsecure(User))
                    {
                        var txterror = secure.Text;
                        try
                        {
                            var configerror = JsonConvert.DeserializeObject<Configuration>(txterror,
                                new Newtonsoft.Json.Converters.VersionConverter(), new StringEnumConverter());
                            configerror!.Error = ConfigFileSecurityError(User, secure);
#if Server
                            Logger?.LogError(configerror.Error);
#endif
                            configerror.Offline = true;
                            configerror.RunningUser = User;
                            configerror.Save(true, false);
                            return null;
                        }
                        catch (JsonException ex)
                        {
#if Server
                            Logger?.LogError(ex, $"Error parsing {Path.GetFileName(ConfigPath)}; {ex.Message}");
#endif
                            return null;
                        }
                    }
                }

                var txt = secure.Text;
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
                            var appFile = SecureFile.Read(file);
                            if (appFile.IsLink)
                            {
#if Server
                                Logger?.LogError($"For security reasons, {Path.GetFileName(file)} must not be a symbolic link. It is not loaded.");
#endif
                                return null;
                            }
                            var json = appFile.Text;
                            if (string.IsNullOrEmpty(json)) return null;
                            Application? app = null;
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
                                return null;
#else
                                throw new InvalidOperationException($"Error parsing {Path.GetFileName(file)}.");
#endif
                            }
                            if (app.Environment == null) app.Environment = new Dictionary<string, string>();
                            app.IsSeparateFile = true;
#if Server
                            if (OSInfo.IsWindows)
                            {
                                if (CheckWindowsAppFile(app, config, file, appFile, secure)) hasError = true;
                            }
                            else
                            {
                                var fileInsecure = appFile.IsInsecure(app.User ?? config.User);
                                // Only root may give an app a group the app's user does not belong to.
                                // A group copied from a root-owned configuration.json is trusted.
                                if (!fileInsecure && !appFile.OwnedByRoot &&
                                    (app.Group != config.Group || !secure.OwnedByRoot) &&
                                    GroupError(app.Group, app.User ?? config.User) is { } groupError)
                                {
                                    app.Offline = true;
                                    app.Status = Status.Error;
                                    app.Error = groupError;
                                    hasError = true;
                                }
                                if (fileInsecure)
                                {
                                    // Retry loading app after one second, in case owner and mode of a freshly created file
                                    // has not yet been set. (The file might have been created by Api's Save method).
                                    /*if (!app.Offline && !app.IsReloading &&
                                        (DateTime.UtcNow - File.GetCreationTimeUtc(file) < TimeSpan.FromSeconds(5)))
                                    {
                                        app.IsReloading = true;
                                        Task.Run(async () =>
                                        {
                                            await Task.Delay(1000);
                                            app.Offline = false;
                                            await LoadAsync();
                                            app.IsReloading = false;
                                        });
                                    }*/
                                    app.Offline = true;
                                    app.Status = Status.Error;
                                    app.Error = $"The app json file {Path.GetFileName(file)} has a different owner than the app's User, or is writable by group or other. It must be writable only by it's owner, and must be owned by the same user specified in it's User json property.";
                                    hasError = true;
                                }
                            }
#endif
                            return app;
                        })
                        .OfType<Application>();
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
        if (HasOldConfig)
        {
            var path = Path.GetDirectoryName(ConfigPath);
            if (path != null && !Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
                if (!OSInfo.IsWindows) Unix.SetFilePermissions(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                else if (WindowsFileSecurity.IsAdministrator()) WindowsFileSecurity.ProtectDirectory(path, AdminGroup);
            }
            File.Copy(ConfigPathOld, ConfigPath, true);
            if (!OSInfo.IsWindows) Unix.SetFilePermissions(ConfigPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            else if (WindowsFileSecurity.IsAdministrator()) WindowsFileSecurity.Protect(ConfigPath, null);

            Directory.Delete(Path.GetDirectoryName(ConfigPathOld)!, true);
        }

        Configuration? config = null;
        if (File.Exists(ConfigPath))
        {
            // Read the configuration file if it exists.
            try
            {
                // The owner and permissions are checked on the handle that the text is read from, not on the path.
                var secure = SecureFile.Read(ConfigPath);
                if (secure.IsLink)
                {
#if Server
                    Logger?.LogError($"For security reasons, {Path.GetFileName(ConfigPath)} must not be a symbolic link. It is not loaded.");
#endif
                    return null; // not written back either: that would write through the link
                }
                {
                    if (secure.IsInsecure(User))
                    {
                        var txterror = secure.Text;
                        try
                        {
                            var configerror = JsonConvert.DeserializeObject<Configuration>(txterror,
                                new Newtonsoft.Json.Converters.VersionConverter(), new StringEnumConverter());
                            configerror!.Error = ConfigFileSecurityError(User, secure);
#if Server
                            Logger?.LogError(configerror.Error);
#endif
                            configerror.Offline = true;
                            configerror.RunningUser = User;
                            configerror.Save(true, false);
                            return null;
                        }
                        catch (JsonException ex)
                        {
#if Server
                            Logger?.LogError(ex, $"Error parsing {Path.GetFileName(ConfigPath)}; {ex.Message}");
#endif
                            return null;
                        }
                    }
                }
                var txt = secure.Text;
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
                    var separateConfigs = Directory.EnumerateFiles(Path.GetDirectoryName(ConfigPath)!, "*.json")
                        .Where(file => file != ConfigPath && file != ConfigPathOld)
                        .Select(file =>
                        {
                            var appFile = SecureFile.Read(file);
                            if (appFile.IsLink)
                            {
#if Server
                                Logger?.LogError($"For security reasons, {Path.GetFileName(file)} must not be a symbolic link. It is not loaded.");
#endif
                                return null;
                            }
                            var json = appFile.Text;
                            if (string.IsNullOrEmpty(json)) return null;
                            Application? app = null;
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
                                return null;
#else
                                throw new InvalidOperationException($"Error parsing {Path.GetFileName(file)}.");
#endif
                            }
                            if (app.Environment == null) app.Environment = new Dictionary<string, string>();
                            app.IsSeparateFile = true;
#if Server
                            if (OSInfo.IsWindows)
                            {
                                if (CheckWindowsAppFile(app, config, file, appFile, secure)) hasError = true;
                            }
                            else
                            {
                                var fileInsecure = appFile.IsInsecure(app.User ?? config.User);
                                // Only root may give an app a group the app's user does not belong to.
                                // A group copied from a root-owned configuration.json is trusted.
                                if (!fileInsecure && !appFile.OwnedByRoot &&
                                    (app.Group != config.Group || !secure.OwnedByRoot) &&
                                    GroupError(app.Group, app.User ?? config.User) is { } groupError)
                                {
                                    app.Offline = true;
                                    app.Status = Status.Error;
                                    app.Error = groupError;
                                    hasError = true;
                                }
                                if (fileInsecure)
                                {
                                    // Retry loading app after one second, in case owner and mode of a freshly created file
                                    // has not yet been set. (The file might have been created by Api's Save method).
                                    /*if (!app.Offline && !app.IsReloading &&
                                        (DateTime.UtcNow - File.GetCreationTimeUtc(file) < TimeSpan.FromSeconds(5)))
                                    {
                                        app.IsReloading = true;
                                        Task.Run(async () =>
                                        {
                                            await Task.Delay(1000);
                                            app.Offline = false;
                                            Load();
                                            app.IsReloading = false;
                                        });
                                    }*/
                                    app.Offline = true;
                                    app.Status = Status.Error;
                                    app.Error = $"The app json file {Path.GetFileName(file)} has a different owner than the app's User, or is writable by group or other. It must be writable only by it's owner, and must be owned by the same user specified in it's User json property.";
                                    hasError = true;
                                }
                            }
#endif
                            return app;
                        })
                        .OfType<Application>();
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

    /// <summary>
    /// Windows: the same check as on Unix for the json file of an app. The file may only be changed by the user that the app runs as and
    /// by administrators (the counterpart of "owned by the user or root, not writable by group or other"), and a group that the user does
    /// not belong to may only come from a file or a configuration.json that administrators own. Takes the app offline and returns true
    /// if something is wrong.
    /// </summary>
    bool CheckWindowsAppFile(Application app, Configuration config, string file, SecureFile appFile, SecureFile configFile)
    {
        if (!OSInfo.IsWindows) return false;
        var user = app.User ?? config.User;
        var problem = appFile.Problem(user);
        if (problem != null)
        {
            app.Offline = true;
            app.Status = Status.Error;
            app.Error = $"The app json file {Path.GetFileName(file)} is not secure: {problem}. It must be changeable only by " +
                $"{(string.IsNullOrEmpty(user) ? "administrators" : $"the app's User {user} and administrators")}.";
            return true;
        }
        if (!appFile.OwnedByRoot &&
            (app.Group != config.Group || !configFile.OwnedByRoot) &&
            GroupError(app.Group, user) is { } groupError)
        {
            app.Offline = true;
            app.Status = Status.Error;
            app.Error = groupError;
            return true;
        }
        return false;
    }

    /// Returns an error message if <paramref name="group"/> may not be used for an app run as <paramref name="user"/>.
    /// The group is applied with sudo as root, so a file that is not owned by root may only name groups the user
    /// belongs to; otherwise its owner could run their app with the privileges of any group (e.g. root or sudo).
    /// </summary>
    static string? GroupError(string? group, string? user)
    {
        // Without a user the app is not started under another account, so the group is not applied.
        if (string.IsNullOrEmpty(group) || string.IsNullOrEmpty(user)) return null;
        // On Windows the group is not applied either; the user has to be a member of it.
        if (OSInfo.IsWindows && !WindowsAccounts.IsUserInGroup(user!, group!)) return $"Group '{group}' is not allowed: only a group that user '{user}' belongs to can be set here. Only an administrator can set other groups.";
        if (OSInfo.IsWindows || Unix.IsUserInGroup(user!, group!)) return null;
        return $"Group '{group}' is not allowed: only a group that user '{user}' belongs to can be set here. Only root can set other groups.";
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
                var config = await LoadConfigAsync() ?? throw new InvalidOperationException("The configuration could not be loaded.");
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
                                else if (GroupError(app.Group ?? config?.Group, owner.Owner) is { } groupError)
                                {
                                    await app.SetStatusAsync(Status.Error, groupError, true, false);
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
                var config = LoadConfig() ?? throw new InvalidOperationException("The configuration could not be loaded.");
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
                if (!OSInfo.IsWindows && !Unix.IsRoot ||
                    OSInfo.IsWindows && !WindowsFileSecurity.IsLocalSystem())
                {
                    if (newApps.Any(app => !string.IsNullOrEmpty(app.User) || !string.IsNullOrEmpty(app.Group)))
                    {
                        Logger?.LogError($"Cannot set User or Group of Application when not running AspNetCoreSharedServer as {(OSInfo.IsWindows ? "SYSTEM" : "root")}.");
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
                        if (owner.Owner != "root" && owner.Owner != User)
                        {
                            foreach (var app in newApps)
                            {
                                if ((app.User ?? config?.User) != owner.Owner)
                                {
                                    app.SetStatus(Status.Error, $"Only {owner.Owner} allowed as User.", true, false);
                                }
                                else if (GroupError(app.Group ?? config?.Group, owner.Owner) is { } groupError)
                                {
                                    app.SetStatus(Status.Error, groupError, true, false);
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
                            oapp.Proxy?.Shutdown(false);
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
                                oapp.Proxy?.Shutdown(false);
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
                        oapp.Proxy?.Shutdown();
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
                    // The listener ends when the app is taken offline or removed. Not when it was changed: the new listener has set the status.
                    await app.SetStoppedAsync();
                }
                catch (Exception)
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
                    // The listener ends when the app is taken offline or removed. Not when it was changed: the new listener has set the status.
                    await app.SetStoppedAsync();
                }
                catch (Exception)
                {
                }
            });
            await app.SetStatusAsync(Status.Running, null, false, false);
        }
#endif
    }

    /// <summary>
    /// Creates the configuration folder if it is not there, with the permissions it must have: on Unix mode 700 (770 with the admin group),
    /// owned by root and <see cref="AdminGroup"/> if root creates it; on Windows full control for SYSTEM and the administrators, and
    /// modify rights for the admin group, if an administrator creates it. The lock files are in this folder too, see <see cref="LockFiles"/>.
    /// </summary>
    internal static void EnsureConfigDirectory(string dir)
    {
        if (Directory.Exists(dir)) return;

        Directory.CreateDirectory(dir);
        if (!OSInfo.IsWindows)
        {
            if (AllowOnlyRootToCreateApplications)
            {
                Unix.SetFilePermissions(dir,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            else if (AllowOnlyAdminsToCreateApplications)
            {
                Unix.SetFilePermissions(dir,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute);
            }
            else throw new NotSupportedException("Either only root or only admin must be allowed to create applications.");
            if (Unix.IsRoot) Unix.SetOwnerAndGroup(dir, "root", AdminGroup);
        }
        else if (WindowsFileSecurity.IsAdministrator())
        {
            WindowsFileSecurity.ProtectDirectory(dir, AllowOnlyAdminsToCreateApplications ? AdminGroup : null);
        }
    }

    public void SaveInternal(bool disableWatcher)
    {
        var dir = Path.GetDirectoryName(ConfigPath)!;
        EnsureConfigDirectory(dir);

        if (Applications == null) Applications = new Applications();
        var oapps = Applications;
        var extapps = oapps.Where(app => app.IsSeparateFile).ToList();
        foreach (var extapp in extapps) Applications.Remove(extapp);
        foreach (var app in Applications)
        {
            if (app.Environment == null || app.Environment.Count == 0) app.Environment = null;
        }
        if (oapps.Count == 0) Applications = null;
        // Everything that is looked at about the file is read through one handle (see SecureFile). A link at the path is removed, and
        // never written through: removing it only unlinks it.
        var configFile = SecureFile.ReadIfExists(ConfigPath);
        if (configFile is { IsLink: true })
        {
            File.Delete(ConfigPath);
            configFile = null;
        }
        if (configFile == null)
        {
            // Windows: an empty file first, in order to set its permissions before the content is written. Unix: SecureFile.Write
            // creates it, with the owner it should have and mode 600.
            if (OSInfo.IsWindows)
            {
                File.WriteAllText(ConfigPath, "");
                if (WindowsFileSecurity.IsAdministrator()) WindowsFileSecurity.Protect(ConfigPath, null);
            }
            Error = null;
        }
        else if (OSInfo.IsWindows)
        {
            Error = configFile.Problem(RunningUser) is { } configProblem ?
                $"For security reasons, the configuration.json must be changeable only by administrators{(!string.IsNullOrEmpty(RunningUser) ? $" and {RunningUser}" : "")} ({configProblem})." : null;
        }
        else
        {
            Error = configFile.IsInsecure(RunningUser) ?
                $"For security reasons, the configuration.json must be owned by {((RunningUser != "root" && RunningUser != null) ? $"root or {RunningUser}" : "root")} and must not have group & other write permission set." : null;
        }
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
        // Unix: root gives the file to root and the admin group. An insecure file is written back with the owner it has, so that it stays
        // insecure and is not made trustworthy by being saved.
        string? configOwner = null, configGroup = null;
        if (!OSInfo.IsWindows && Unix.IsRoot)
        {
            if (Error == null) (configOwner, configGroup) = ("root", AdminGroup);
            else (configOwner, configGroup) = (configFile?.Owner, configFile?.Group);
        }
        SecureFile.Write(ConfigPath, json, configOwner, configGroup, configFile, Error != null);
        foreach (var extapp in extapps)
        {
            var file = Path.Combine(Path.GetDirectoryName(ConfigPath)!, $"{extapp.Name}.json");
            bool fileIsInsecure = false;
            // A link at the path is removed and never written through. The file is read through one handle.
            var current = SecureFile.ReadIfExists(file);
            if (current is { IsLink: true })
            {
                File.Delete(file);
                current = null;
            }
            if (current == null)
            {
                // Windows: an empty file first, in order to set permissions before the content is written. Unix: SecureFile.Write creates it.
                if (OSInfo.IsWindows) File.WriteAllText(file, "");
            }
            else
            {
                if (OSInfo.IsWindows &&
                    current.Problem(extapp.User ?? RunningUser) is { } windowsProblem)
                {
                    // File can be changed by someone else than the user of the app and the administrators
                    fileIsInsecure = true;
                    var user = extapp.User ?? RunningUser;
                    extapp.Error = $"For security reasons, the {extapp.Name}.json must be changeable only by {(string.IsNullOrEmpty(user) ? "administrators" : $"{user} and administrators")} ({windowsProblem}).";
                    extapp.Offline = true;
                    extapp.Status = Status.Error;
                }
                if (!OSInfo.IsWindows)
                {
                    if (current.IsInsecure(extapp.User ?? RunningUser))
                    {
                        // File has insecure permissions or owner
                        fileIsInsecure = true;
                        var user = extapp.User ?? RunningUser ?? "root";
                        extapp.Error = $"For security reasons, the {extapp.Name}.json must be owned by {(user != "root" ? $"root or {user}" : "root")} and must not have group & other write permission set.";
                        extapp.Offline = true;
                        extapp.Status = Status.Error;
                    }
                }
            }
            Applications.Add(extapp); // re-add app to Applications
            if (extapp.Environment == null || extapp.Environment.Count == 0) extapp.Environment = null;
            var extjson = JsonConvert.SerializeObject(extapp, Formatting.Indented,
                new JsonSerializerSettings { DefaultValueHandling = DefaultValueHandling.Ignore });
            if (extapp.Environment == null) extapp.Environment = new Dictionary<string, string>();
            if (OSInfo.IsWindows && !fileIsInsecure && WindowsFileSecurity.IsAdministrator())
            {
                try { WindowsFileSecurity.Protect(file, extapp.User ?? RunningUser); }
                catch { }
            }
            // Unix: root gives the file to the app's user. An insecure file is written back with the owner it has, so that it stays
            // insecure and is not made trustworthy by being saved.
            string? fileOwner = null, fileGroup = null;
            if (!OSInfo.IsWindows && Unix.IsRoot)
            {
                if (fileIsInsecure) (fileOwner, fileGroup) = (current?.Owner, current?.Group);
                else (fileOwner, fileGroup) = (extapp.User ?? RunningUser ?? "root", AdminGroup);
            }
            SecureFile.Write(file, extjson, fileOwner, fileGroup, current, fileIsInsecure);
        }
        // set permissions
        if (!OSInfo.IsWindows)
        {
            var configDir = Path.GetDirectoryName(ConfigPath);
            if (AllowOnlyRootToCreateApplications)
            {
                if (Unix.IsRoot)
                {
                    Unix.SetFilePermissions(configDir!, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                    Unix.SetOwnerAndGroup(configDir!, "root", AdminGroup);
                    // the owner and mode of the configuration.json were set when it was written, see SecureFile.Write
                }
            }
            else if (AllowOnlyAdminsToCreateApplications)
            {
                if (Unix.IsRoot)
                {
                    Unix.SetFilePermissions(configDir!, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute);
                    Unix.SetOwnerAndGroup(configDir!, "root", AdminGroup);
                    // the owner and mode of the configuration.json were set when it was written, see SecureFile.Write
                }
            }
            else throw new NotSupportedException("Either only root or only admins must be allowed to create applications.");
        }
        else if (WindowsFileSecurity.IsAdministrator())
        {
            // The counterpart of owner root and mode 700/600: only administrators (and the admin group, for the folder) may change them.
            try
            {
                WindowsFileSecurity.ProtectDirectory(Path.GetDirectoryName(ConfigPath)!, AllowOnlyAdminsToCreateApplications ? AdminGroup : null);
                if (Error == null) WindowsFileSecurity.Protect(ConfigPath, null);
            }
            catch { }
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
                        oapp.Proxy?.Shutdown(false);
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
                    app.Proxy?.Shutdown();
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

    static Configuration? configuration = null;
    public static Configuration Current => configuration ??= new Configuration();

    static Configuration() { }

    static FileSystemWatcher? watcher = null;
    CancellationTokenSource? reloadCts = new CancellationTokenSource();

    public void Watch()
    {
        var dir = Path.GetDirectoryName(ConfigPath)!;
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

        watcher = new FileSystemWatcher(dir, "*.json")
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName
                  | NotifyFilters.DirectoryName
                  | NotifyFilters.LastWrite
                  | NotifyFilters.Size
                  | NotifyFilters.Security,
            EnableRaisingEvents = true
        };

        // The server saves a file by renaming a new one over it, and an editor may do the same, which is a rename and not a change.
        FileSystemEventHandler onChange = (_, __) =>
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
        watcher.Changed += onChange;
        watcher.Created += onChange;
        watcher.Renamed += (sender, e) => onChange(sender, e);
        watcher.EnableRaisingEvents = true;
        Load();
    }
    public async Task WatchAsync()
    {
        var dir = Path.GetDirectoryName(ConfigPath)!;
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

        watcher = new FileSystemWatcher(dir, "*.json");
        // The server saves a file by renaming a new one over it, and an editor may do the same, which is a rename and not a change.
        FileSystemEventHandler onChange = (_, __) =>
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
        watcher.Changed += onChange;
        watcher.Created += onChange;
        watcher.Renamed += (sender, e) => onChange(sender, e);
        watcher.EnableRaisingEvents = true;

        await LoadAsync();
    }

    /// <summary>
    /// The status of the applications when the server is stopped: Stopped, except for an application that is in Error. That one keeps its
    /// error and its message: stopping the server does not repair it, and the next start of the server and aspnet-server status have to
    /// tell why it does not run.
    /// </summary>
    static void MarkStopped(IEnumerable<Application> apps)
    {
        foreach (var app in apps)
        {
            if (app.Status != Status.Error) app.Status = Status.Stopped;
        }
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
            MarkStopped(apps);
            config?.Save(false, false);
        }
        // Select(Task.Run(...)), not Select(async app => Task.Run(...)): the async lambda makes a sequence of Task<Task> whose outer tasks
        // are done at once, so nothing would wait for the work, and Task.WaitAll throws an ArrayTypeMismatchException for such a
        // sequence when it is made from an empty array (as it is when there is no configuration).
        var tasks = apps
            .Where(app => app.Proxy != null)
            .Select(app => Task.Run(() =>
            {
                app.Proxy?.Shutdown();
                app.Proxy = null;
            }))
            .ToArray();
        Task.WaitAll(tasks);

        // The same as in ShutdownAsync: the file would say Stopping for ever.
        using (var mutex = Lock())
        {
            MarkStopped(apps);
            Save(true, false);
        }
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
            // The applications of this configuration, like Shutdown() has it: they are the ones that have a Proxy. The applications of the
            // configuration that has just been loaded are new objects and never have one, so none of the proxies was shut down.
            apps = Applications?.ToList() ?? Enumerable.Empty<Application>();
            MarkStopped(apps);
            await (config?.SaveAsync(false, false) ?? Task.CompletedTask);
        }
        // Select(Task.Run(...)), not Select(async app => Task.Run(...)): the async lambda makes a sequence of Task<Task> whose outer tasks
        // are done at once, so nothing would wait for the work, and Task.WaitAll throws an ArrayTypeMismatchException for such a
        // sequence when it is made from an empty array (as it is when there is no configuration).
        var tasks = apps
            .Where(app => app.Proxy != null)
            .Select(app => Task.Run(async () =>
            {
                await (app.Proxy?.ShutdownAsync() ?? Task.CompletedTask);
                app.Proxy = null;
            }))
            .ToArray();
        await Task.WhenAll(tasks);

        // While the proxies shut their applications down they record Stopping, and nothing records that this is over: the file would
        // say Stopping for ever, for the next start of the server and for aspnet-server status.
        using (var mutex = await LockAsync())
        {
            MarkStopped(apps);
            await SaveAsync(true, false);
        }
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
        // Select(Task.Run(...)), not Select(async app => Task.Run(...)): the async lambda makes a sequence of Task<Task> whose outer tasks
        // are done at once, so nothing would wait for the work, and Task.WaitAll throws an ArrayTypeMismatchException for such a
        // sequence when it is made from an empty array (as it is when there is no configuration).
        var tasks = apps
            .Where(app => app.Proxy != null)
            .Select(app => Task.Run(() =>
            {
                app.Proxy?.Shutdown(false);
                app.Proxy = null;
            }))
            .ToArray();
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
            // The Tasks themselves, so that the configuration is not loaded again before the proxies are down (see ShutdownAsync).
            var tasks = apps
                .Where(app => app.Proxy != null)
                .Select(app => Task.Run(async () =>
                {
                    await (app.Proxy?.ShutdownAsync(false) ?? Task.CompletedTask);
                    app.Proxy = null;
                }))
                .ToArray();
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
    public new Application? this[string name]
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
    public string? WorkingDirectory { get; set; } = null;
    [DefaultValue(null)]
    public string? Path { get; set; } = null;
    [DefaultValue("")]
    public string Arguments { get; set; } = string.Empty;
    [DefaultValue(null)]
    public bool? EnableHttp3 { get; set; } = null;
    [DefaultValue(null)]
    public bool? EnableProxyHeader { get; set; } = null;
    [DefaultValue(null)]
    public bool? EnableProxyV2Header { get; set; } = null;

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
    [DefaultValue(Status.Stopped), JsonConverter(typeof(StringEnumConverter))]
    public Status Status { get; set; } = Status.Stopped;
    [DefaultValue(null)]
    public string? Error { get; set; } = null;
    [DefaultValue(Command.None), JsonConverter(typeof(StringEnumConverter))]
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
        app.EnableProxyHeader = EnableProxyHeader;
        app.EnableProxyV2Header = EnableProxyV2Header;
#if Server
        app.Proxy = Proxy;
#endif
    }

    internal void SetStatus(Status status, string? error, bool disable = false, bool load = true)
    {
        using var mutex = AspServer.Lock();
        if (load) Configuration.Current.Load(false);
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
            config.Save(true, false);
            if (disableChanged && load) _ = Task.Run(() => Configuration.Current.Load(false));
        }
    }
    /// <summary>
    /// Records that the app is no longer listening, once its listener has ended: the status becomes Stopped if the app is offline (or the
    /// server) and the status is still Running. Anything else is left alone: an Error with its message, or the Running of a listener that
    /// replaced this one after the app was changed.
    /// </summary>
    internal async Task SetStoppedAsync()
    {
        using var mutex = await AspServer.LockAsync();
        var config = Configuration.Current;
        var app = config.Applications?[Name];
        if (app != null && app.Status == Status.Running && (app.Offline || config.Offline))
        {
            app.Status = Status.Stopped;
            await config.SaveAsync(true, false);
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
            if (disableChanged && load) _ = Task.Run(async () => await Configuration.Current.LoadAsync(false));
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
                AssemblyDefinition? assembly = null;
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

    static void SetOffline(string? appId, bool disabled)
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
            if (config?.Applications?[appId] is { } target && target.Offline != disabled)
            {
                target.Offline = disabled;
                config.Save(true, false);
                Configuration.Load(false);
            }
        }
    }
    static async Task SetOfflineAsync(string? appId, bool disabled)
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
            if (config?.Applications?[appId] is { } target && target.Offline != disabled)
            {
                target.Offline = disabled;
                await config.SaveAsync(true, false);
                await Configuration.LoadAsync(false);
            }
        }
    }

    public static void Start(string? appId = null) => SetOffline(appId, false);
    public static void Stop(string? appId = null) => SetOffline(appId, true);
    public static void Restart(string? appId = null)
    {
        Stop(appId);
        Start(appId);
    }
    public static async Task StartAsync(string? appId = null) => await SetOfflineAsync(appId, false);
    public static async Task StopAsync(string? appId = null) => await SetOfflineAsync(appId, true);
    public static async Task RestartAsync(string? appId = null)
    {
        await StopAsync(appId);
        await StartAsync(appId);
    }

    public static Status Status(string? appId = null)
    {
        using var mutex = Configuration.Lock();
        if (appId == null)
        {
            return Configuration.Offline ? AspNetCoreSharedServer.Status.Running : AspNetCoreSharedServer.Status.Stopped;
        }
        else
        {
            return Configuration.Applications?[appId]?.Status ??
                throw new KeyNotFoundException($"There is no application {appId}");
        }
    }
    public static async Task<Status> StatusAsync(string? appId = null)
    {
        using var mutex = await Configuration.LockAsync();
        if (appId == null)
        {
            return Configuration.Offline ? AspNetCoreSharedServer.Status.Running : AspNetCoreSharedServer.Status.Stopped;
        }
        else
        {
            return Configuration.Applications?[appId]?.Status ??
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