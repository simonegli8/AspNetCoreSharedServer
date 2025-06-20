using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System.ComponentModel;

namespace AspNetCoreSharedServer;

[JsonConverter(typeof(StringEnumConverter))]
public enum Command
{
	None = 0,
	Shutdown = 1,
}
public class Configuration
{
	public class NamedMutex: IDisposable
	{
		public const string MutexPath = "Global\\aspnetcore.lock";

		private static Mutex mutex = new Mutex(false, MutexPath);
		public NamedMutex()
		{
			mutex.WaitOne();
		}
		public void Dispose()
		{
			mutex.ReleaseMutex();
		}
	}
	[JsonIgnore]
	public string ConfigPath => Environment.OSVersion.Platform == PlatformID.Win32NT ?
		Path.Combine(Environment.CurrentDirectory, "applications.json") :
		"/etc/aspnetcore/applications.json";

#if Server
	[JsonIgnore]
	public ILogger<Worker> Logger = null;
#endif
	public List<Application> Applications { get; set; } = new List<Application>();
	public TimeSpan? IdleTimeout { get; set; } = TimeSpan.FromMinutes(5);
	public TimeSpan? Recycle { get; set; } = TimeSpan.FromMinutes(20);
	public bool EnableHttp3 { get; set; } = true;
	[DefaultValue(Command.None)]
	public Command Command { get; set; } = Command.None;

	public void Load()
	{
		using (var mutex = new NamedMutex())
		{
			try
			{
				Configuration? config = null;
				if (File.Exists(ConfigPath))
				{
					// Read the configuration file if it exists.
					try
					{
						var txt = File.ReadAllText(ConfigPath);
						config = JsonConvert.DeserializeObject<Configuration>(txt);
#if Server
						Logger.LogInformation("Loaded configuration file {ConfigPath}", ConfigPath);
#endif
					}
					catch (Exception ex)
					{
#if Server
						Logger.LogError(ex, "Failed to read configuration file {ConfigPath}", ConfigPath);
#endif
					}
				}
				else
				{
					config = new Configuration();
				}

#if Server
				if (config.Command == Command.Shutdown)
				{
					// If the command is Shutdown, we should shutdown the server.
					Logger.LogInformation("Received shutdown command, shutting down server.");
					Shutdown();

					// remove Shutdown command from config
					config.Command = Command.None;
					config.Save(true);
					
					return;
				}

				IdleTimeout = config.IdleTimeout;
				Recycle = config.Recycle;
				EnableHttp3 = config.EnableHttp3;

				var oldApps = Applications
					.OrderBy(app => app.Name)
					.ToList();
				var newApps = (config?.Applications ?? new List<Application>())
					.OrderBy(app => app.Name)
					.ToList();
				int i = 0, oi = 0;
				for (i = 0; i < newApps.Count; i++)
				{
					var app = newApps[i];
					while (oi < oldApps.Count && string.Compare(oldApps[oi].Name, app.Name) < 0)
					{
						var oapp = oldApps[oi];
						if (oapp.Server != null)
						{
							oapp.Server.Shutdown();
							oapp.Server = null;
						}
						oi++;
					}
					if (oi < oldApps.Count && oldApps[oi].Name == app.Name)
					{
						var oapp = oldApps[oi];
						if (oapp.Assembly != app.Assembly || oapp.Urls != app.Urls || oapp.Arguments != app.Arguments ||
							!oapp.Environment.Keys.All(key => app.Environment.ContainsKey(key) && app.Environment[key] == oapp.Environment[key]) ||
							oapp.IdleTimeout != app.IdleTimeout || oapp.Recycle != app.Recycle)
						{
							if (oapp.Server != null)
							{
								oapp.Server.Shutdown();
								oapp.Server = null;
							}
							Listen(app);
						}
						else
						{
							app.Server = oapp.Server;
						}
						oi++;
					}
					else
					{
						Listen(app);
					}
				}

				while (oi < oldApps.Count)
				{
					var oapp = oldApps[oi];
					if (oapp.Server != null)
					{
						oapp.Server.Shutdown();
						oapp.Server = null;
					}
					oi++;
				}

				Applications = newApps;

				if (!File.Exists(ConfigPath)) Save(true);
				else SaveIfDirty(true);
#endif
			}
			finally
			{
			}
		}
	}

	public void Listen(Application app)
	{
#if Server
		var server = new Server(app);
		Task.Run(() => server.ListenAsync());
#endif
	}
	public void Save(bool disableWatcher = false)
	{
		using (var mutex = new NamedMutex())
		{
			var txt = JsonConvert.SerializeObject(this, Formatting.Indented, 
				new JsonSerializerSettings { DefaultValueHandling = DefaultValueHandling.Ignore });
			if (watcher != null && disableWatcher) watcher.EnableRaisingEvents = false;
			File.WriteAllText(ConfigPath, txt);
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

	public void Add(Application app)
	{
		using (var mutex = new NamedMutex())
		{
			Load();
			var oapp = Applications.FirstOrDefault(a => a.Name == app.Name);
			if (oapp != null)
			{
				if (oapp.Assembly != app.Assembly || oapp.Urls != app.Urls || oapp.Arguments != app.Arguments ||
				!oapp.Environment.Keys.All(key => app.Environment.ContainsKey(key) && app.Environment[key] == oapp.Environment[key]) ||
				oapp.IdleTimeout != app.IdleTimeout || oapp.Recycle != app.Recycle)
				{
#if Server
					if (oapp.Server != null)
					{
						oapp.Server.Shutdown();
						oapp.Server = null;
					}
#endif
					Applications.Remove(oapp);
					Applications.Add(app);
					Listen(app);
				}
				else
				{
#if Server
					app.Server = oapp.Server;
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
		using (var mutex = new NamedMutex())
		{
			Load();
			var app = Applications.FirstOrDefault(a => a.Name == name);
			if (app != null)
			{
#if Server
				if (app.Server != null)
				{
					app.Server.Shutdown();
					app.Server = null;
				}
#endif
				Applications.Remove(app);
				Save(true);
			}
		}
	}
	public void Update(Application app) => Add(app);
	public void Remove(Application app) => Remove(app.Name);

	public static Configuration Current = new Configuration();

	FileSystemWatcher? watcher = null;
	public void Watch()
	{
		var dir = Path.GetDirectoryName(ConfigPath);
		if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

		watcher = new FileSystemWatcher(dir, Path.GetFileName(ConfigPath));
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
			if (app.Server != null)
			{
				app.Server.Cancel.Cancel();
				app.Server.Shutdown();
				app.Server = null;
			}
		}
#else
		Command = Command.Shutdown;
		Save(true);
#endif
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
	public string Urls { get; set; } = string.Empty;
	public Dictionary<string, string> Environment { get; set; } = new Dictionary<string, string>();
	public string Assembly { get; set; } = string.Empty;
	public string Arguments { get; set; } = string.Empty;
	[DefaultValue(null)]
	public bool? EnableHttp3 { get; set; } = null;
	[DefaultValue(null)]
	public TimeSpan? IdleTimeout { get; set; } = null;
	[DefaultValue(null)]
	public TimeSpan? Recycle { get; set; } = null;

#if Server
	[JsonIgnore]
	public Server? Server { get; set; } = null;
#endif
}
