using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using Mono.Cecil;
using NeoSmart.AsyncLock;
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
    public const bool AllowOnlyRootToCreateApplications = true;
    public class SyslogConfiguration
    {
        [DefaultValue(null)]
        public string? Host { get; set; } = null;
        [DefaultValue(null)]
        public int? Port { get; set; } = null;
        [DefaultValue(ProtocolType.Unknown)]
        public ProtocolType Protocol { get; set; } = ProtocolType.Unknown;

    }
    public class NamedMutex : IDisposable
    {
        public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
        public const string MutexName = "Global\\aspnetcore.lock";

        static Mutex? mutex = null;
        static AsyncLock Lock = new AsyncLock();
        static Mutex Mutex
        {
            get
            {
                if (mutex != null) return mutex;

                const string MutexDiskPath = "/tmp/.dotnet/shm/global";
                if ((OSInfo.IsLinux || OSInfo.IsMac) && !Directory.Exists(MutexDiskPath))
                {
                    Directory.CreateDirectory(MutexDiskPath);
                    Unix.SetFilePermissions("/tmp/.dotnet", (UnixFileMode)0x1ff, true);
                }

                return mutex = new Mutex(false, MutexName);
            }
        }
        public NamedMutex()
        {
            using (Lock.Lock())
            {
                try
                {
                    Mutex.WaitOne(Timeout);
                }
                catch (AbandonedMutexException) { }
            }
        }
        public void Dispose()
        {
            using (Lock.Lock()) Mutex.ReleaseMutex();
        }
    }
    static int StartPort = 10000;
    public static int FindFreePort(bool temporary = false)
    {
        const int EndPort = 49000;
        int port = 0;
        Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            Configuration.Current.Load();
            var apps = Configuration.Current.Applications;
            IPEndPoint localEP;
            if (temporary)
            {
                localEP = new IPEndPoint(IPAddress.Any, 0);
                socket.Bind(localEP);
            }
            else
            {
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
    public string ConfigPath => OSInfo.IsWindows ? ConfigPathOld :
        Directory.Exists("/etc/aspnetcore") ? ConfigPathOld : "/etc/aspnet-server/applications.json";

#if Server
    [JsonIgnore]
    public ILogger<Worker> Logger { get; set; } = null;
#endif
    public Applications Applications { get; set; } = new Applications();
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
    [DefaultValue(null)]
    public SyslogConfiguration? Syslog { get; set; } = null;

    [DefaultValue(Command.None)]
    public Command Command { get; set; } = Command.None;
    public bool IsShuttingDown = false;
    [DefaultValue(false)]
    public bool Disabled { get; set; } = false;
    [DefaultValue(typeof(TimeSpan), "00:05:00")]
    public TimeSpan FailureInterval { get; set; } = TimeSpan.FromMinutes(5);
    [DefaultValue(5)]
    public int FailureLimit { get; set; } = 5;
    bool loadEntered = false;

    public NamedMutex Mutex => new NamedMutex();
    private Configuration? LoadRaw()
    {
        Configuration? config = null;
        if (File.Exists(ConfigPath))
        {
            // Read the configuration file if it exists.
            try
            {
                var txt = File.ReadAllText(ConfigPath);
                config = JsonConvert.DeserializeObject<Configuration>(txt,
                    new Newtonsoft.Json.Converters.VersionConverter(), new StringEnumConverter());
                if (config.Applications == null) config.Applications = new Applications();

                foreach (var app in config.Applications) app.IsSeparateFile = false;
                var separateConfigs = Directory.EnumerateFiles(Path.GetDirectoryName(ConfigPath), "*.json")
                    .Where(file => file != ConfigPath)
                    .Select(file =>
                    {
                        var json = File.ReadAllText(file);
                       return JsonConvert.DeserializeObject<Application>(json,
                            new Newtonsoft.Json.Converters.VersionConverter(), new StringEnumConverter());
                    });
                foreach (var sconfig in separateConfigs) config.Applications.Add(sconfig);

#if Server
                Logger?.LogInformation("Loaded configuration file {ConfigPath}", ConfigPath);
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
            config = new Configuration();
        }
        return config;
    }
    public Configuration? LoadOnly()
    {
        using (var mutex = Mutex)
        {
            return LoadRaw();
        }
    }
    public void Load()
    {
        if (loadEntered || IsShuttingDown) return; // Prevent re-entrancy
        loadEntered = true;

        using (var mutex = Mutex)
        {
            try
            {
                var config = LoadRaw();
#if Server
                if (config.Command == Command.Shutdown)
                {
                    // remove Shutdown command from config
                    config.Command = Command.None;
                    config.Save(true);

                    // If the command is Shutdown, we should shutdown the server.
                    Logger.LogInformation("Received shutdown command, shutting down server.");
                    Shutdown();

                    return;
                }
                else if (config.Command == Command.Restart)
                {
                    // remove restart command from config
                    config.Command = Command.None;
                    config.Save(true);

                    // If the command is Restart, we should restart the server.
                    Logger.LogInformation("Received restart command, restarting server.");
                    Restart();

                    return;
                }

                IdleTimeout = config.IdleTimeout;
                Recycle = config.Recycle;
                EnableHttp3 = config.EnableHttp3;
                var ouser = User;
                User = config.User;
                var oldDisabled = Disabled;
                Disabled = config.Disabled;
                Syslog = config.Syslog;
                Command = Command.None;

                var oldApps = Applications
                    .OrderBy(app => app.Name)
                    .ToList();
                var newApps = (config?.Applications ?? new Applications())
                    .OrderBy(app => app.Name)
                    .ToList();

                // If not runnig as root, forbid User & Group settings
                if (!OSInfo.IsWindows && Unix.getuid() != 0)
                {
                    if (newApps.Any(app => !string.IsNullOrEmpty(app.User) || !string.IsNullOrEmpty(app.Group)))
                    {
                        Logger.LogError("Cannot set User or Group of Application when not running AspNetCoreSharedServer as root.");
                        return;
                    }
                } /*else if (!OSInfo.IsWindows && Syscall.getuid() == 0)
				{
					if (watcher != null) watcher.EnableRaisingEvents = false;
					// If running as root, set permissions to read/write for root only
					Syscall.chmod(Path.GetDirectoryName(ConfigPath), FilePermissions.S_IRUSR | FilePermissions.S_IWUSR);
					Syscall.chmod(ConfigPath, FilePermissions.S_IRUSR | FilePermissions.S_IWUSR);
					Thread.Sleep(100);
					if (watcher != null) watcher.EnableRaisingEvents = true;
				}*/

                int i = 0, oi = 0;
                for (i = 0; i < newApps.Count; i++)
                {
                    var app = newApps[i];
                    while (oi < oldApps.Count && string.Compare(oldApps[oi].Name, app.Name) < 0)
                    {
                        var oapp = oldApps[oi];
                        if (oapp.Proxy != null)
                        {
                            oapp.Proxy.Shutdown();
                            oapp.Proxy = null;
                        }
                        oi++;
                    }
                    if (oi < oldApps.Count && oldApps[oi].Name == app.Name)
                    {
                        var oapp = oldApps[oi];
                        if (oapp.Assembly != app.Assembly || oapp.Urls != app.Urls || oapp.Arguments != app.Arguments ||
                            oapp.ListenUrls != app.ListenUrls || oapp.User != app.User || oapp.Group != app.Group ||
                            !oapp.Environment.Keys.All(key => app.Environment.ContainsKey(key) && app.Environment[key] == oapp.Environment[key]) ||
                            oapp.IdleTimeout != app.IdleTimeout || oapp.Recycle != app.Recycle || (oapp.Disabled || oldDisabled) != (app.Disabled || Disabled))
                        {
                            if (oapp.Proxy != null)
                            {
                                oapp.Proxy.Shutdown();
                                oapp.Proxy = null;
                            }
                            app.CopyTo(oapp);
                            Listen(oapp);
                        }
                        oi++;
                    }
                    else
                    {
                        Listen(app);
                        Applications.Add(app);
                    }
                }

                while (oi < oldApps.Count)
                {
                    var oapp = oldApps[oi];
                    if (oapp.Proxy != null)
                    {
                        oapp.Proxy.Shutdown();
                        oapp.Proxy = null;
                    }
                    Applications.Remove(oapp);
                    oi++;
                }

                if (!File.Exists(ConfigPath)) Save(true);
                else SaveIfDirty(true);
#else
				IdleTimeout = config.IdleTimeout;
				Recycle = config.Recycle;
				EnableHttp3 = config.EnableHttp3;
				Applications = config.Applications;
				User = config.User;
				Disabled = config.Disabled;
#endif
            }
            finally
            {
                loadEntered = false; // Reset the flag to allow future loads
            }
        }
    }

    public void Listen(Application app)
    {
#if Server
        if (!IsShuttingDown && !app.Disabled && !Disabled)
        {
            var server = new Proxy(app);
            Task.Run(() => server.ListenAsync());
            app.SetStatus(Status.Running, null);
        }
#endif
    }

    public void Save(bool disableWatcher = false)
    {
        var dir = Path.GetDirectoryName(ConfigPath);
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
            if (AllowOnlyRootToCreateApplications)
            {
                Unix.SetFilePermissions(ConfigPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            else
            {
                Unix.SetFilePermissions(ConfigPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute);
            }
        }
         
        using (var mutex = Mutex)
        {
            var oapps = Applications;
            var extapps = oapps.Where(app => app.IsSeparateFile).ToList();
            foreach (var extapp in extapps) Applications.Remove(extapp);
            var json = JsonConvert.SerializeObject(this, Formatting.Indented,
                new JsonSerializerSettings { DefaultValueHandling = DefaultValueHandling.Ignore });
            foreach (var extapp in extapps)
            {
                Applications.Add(extapp);
                var extjson = JsonConvert.SerializeObject(extapp, Formatting.Indented,
                    new JsonSerializerSettings { DefaultValueHandling = DefaultValueHandling.Ignore });
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(ConfigPath), $"{extapp.Name}.json"), extjson);
            }
            if (watcher != null && disableWatcher) watcher.EnableRaisingEvents = false;
            File.WriteAllText(ConfigPath, json);
            // If runnig as root, set permissions to read/write for root only
            if (!OSInfo.IsWindows /* && Unix.getuid() == 0 */)
            {
                Unix.chmod(Path.GetDirectoryName(ConfigPath), 0x180);
                Unix.chmod(ConfigPath, 0x180);
            }
            Thread.Sleep(100);
            if (watcher != null && disableWatcher) watcher.EnableRaisingEvents = true;
            foreach (var app in Applications) app.Dirty = false;
        }
    }
    public void SaveIfDirty(bool disableWatcher = false)
    {
        if (Applications.Any(app => app.Dirty))
        {
            Save(disableWatcher);
        }
    }
    /*public void ReportError(string message, Exception ex)
	{
		var dir = Path.GetDirectoryName(ConfigPath);
		if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

		File.WriteAllText(Path.ChangeExtension(ConfigPath, "error"), message + Environment.NewLine + ex.ToString());
	}
	public void RemoveError()
	{
		var file = Path.ChangeExtension(ConfigPath, "error");
		if (File.Exists(file)) File.Delete(file);
	}
	public string? ReadError()
	{
		var file = Path.ChangeExtension(ConfigPath, "error");
		if (File.Exists(file)) return File.ReadAllText(file);
		return null;
	}*/

    public void Add(Application app)
    {
        using (var mutex = Mutex)
        {
            Load();
            var oapp = Applications[app.Name];
            if (oapp != null)
            {
                if (oapp.Assembly != app.Assembly || oapp.Urls != app.Urls || oapp.Arguments != app.Arguments ||
                    oapp.ListenUrls != app.ListenUrls || oapp.User != app.User || oapp.Group != app.Group ||
                    !oapp.Environment.Keys.All(key => app.Environment.ContainsKey(key) && app.Environment[key] == oapp.Environment[key]) ||
                    oapp.IdleTimeout != app.IdleTimeout || oapp.Recycle != app.Recycle ||
                    !Disabled && (oapp.Disabled != app.Disabled))
                {
#if Server
                    if (oapp.Proxy != null)
                    {
                        oapp.Proxy.Shutdown();
                        oapp.Proxy = null;
                    }
#endif
                    Applications.Remove(oapp);
                    Applications.Add(app);
                    Listen(app);
                }
                else
                {
#if Server
                    app.Proxy = oapp.Proxy;
#endif
                    Applications.Remove(oapp);
                    Applications.Add(app);
                    return; // No changes, nothing to do.
                }
            }
            else
            {
                Applications.Add(app);
                Listen(app);
            }
            Save(true);
        }
    }

    public void Remove(string name)
    {
        using (var mutex = Mutex)
        {
            Load();
            var app = Applications.FirstOrDefault(a => a.Name == name);
            if (app != null)
            {
#if Server
                if (app.Proxy != null)
                {
                    app.Proxy.Shutdown();
                    app.Proxy = null;
                }
#endif
                Applications.Remove(app);
                Save(true);
            }
        }
    }
    public void Update(Application app) => Add(app);
    public void Remove(Application app) => Remove(app.Name);

    static Configuration configuration = null;
    public static Configuration Current => configuration ??= new Configuration();

    static Configuration() { }

    FileSystemWatcher? watcher = null;
    public void Watch()
    {
        var dir = Path.GetDirectoryName(ConfigPath);
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

        watcher = new FileSystemWatcher(dir, "*.json");
        watcher.Changed += (s, e) =>
            Load();
        watcher.EnableRaisingEvents = true;
        Load();
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
        foreach (var app in Applications)
        {
            if (app.Proxy != null)
            {
                app.Proxy.Cancel.Cancel();
                app.Proxy.Shutdown();
                app.Proxy = null;
            }
        }
#else
		Command = Command.Shutdown;
		Save(true);
#endif
    }
    public void Restart()
    {
#if Server
        foreach (var app in Applications)
        {
            if (app.Proxy != null)
            {
                app.Proxy.Cancel.Cancel();
                app.Proxy.Shutdown();
                app.Proxy = null;
            }
        }
        Load();
#else
		Command = Command.Restart;
		Save(true);
#endif
    }

}
public class Applications : KeyedCollection<string, Application>
{
    protected override string GetKeyForItem(Application item)
    {
        if (item.Name == "applications") throw new NotSupportedException("Application name \"applications\" is reserved.");
        return item.Name;
    }
    protected override void InsertItem(int index, Application item)
    {
        if (item.Name == "applications") throw new NotSupportedException("Application name \"applications\" is reserved.");
        base.InsertItem(index, item);
    }
    protected override void SetItem(int index, Application item)
    {
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
    public Dictionary<string, string> Environment { get; set; } = new Dictionary<string, string>();
    public string Assembly { get; set; } = string.Empty;
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
    public bool Disabled { get; set; } = false;
    [DefaultValue(Status.Running)]
    public Status Status { get; set; } = Status.Running;
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
    public void CopyTo(Application app)
    {
        app.Arguments = Arguments;
        app.Assembly = Assembly;
        app.Command = Command;
        app.Dirty = Dirty;
        app.Disabled = Disabled;
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
#if Server
        app.Proxy = Proxy;
#endif
    }

    internal void SetStatus(Status status, string? error, bool disable = false)
    {
        using var mutex = AspServer.Mutex;
        var config = Configuration.Current.LoadOnly();
        var app = config.Applications.FirstOrDefault(a => a.Name == Name);
        if (app != null && app.Status != status && app.Error != error)
        {
            bool suppressFileWatcher = disable == app.Disabled;
            app.Status = status; app.Error = error;
            if (disable) app.Disabled = true;
            config.Save(suppressFileWatcher);
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

    static void SetDisabled(string appId, bool disabled) {
        bool wasDisabled;
        if (appId == null)
        {
            using var mutex = Configuration.Mutex;
            var config = Configuration.LoadOnly();
            config.Disabled = disabled;
            config.Save(true);
            Configuration.Load();
        }
        else
        {
            using var mutex = new Configuration.NamedMutex();
            var config = Configuration.LoadOnly();
            config.Applications[appId].Disabled = disabled;
            config.Save(true);
            Configuration.Load();
        }
    }
    public static void Start(string appId = null) => SetDisabled(appId, false);
    public static void Stop(string appId = null) => SetDisabled(appId, false);
    public static void Restart(string appId = null)
    {
        Stop(appId);
        Start(appId);
    }
    public static Status Status(string appId = null)
    {
        using var mutex = new Configuration.NamedMutex();
        if (appId == null)
        {
            return Configuration.Disabled ? AspNetCoreSharedServer.Status.Running : AspNetCoreSharedServer.Status.Stopped;
        } else
        {
            return Configuration.Applications[appId].Status;
        }
    }

    public static void Shutdown() => Configuration.Shutdown();
    public static int FindFreePort() => Configuration.FindFreePort();
    public static Configuration.NamedMutex Mutex => Configuration.Mutex;
}