using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using Newtonsoft.Json;

namespace AspNetCoreSharedServer;

public class Configuration
{
	public class NamedMutex: IDisposable
	{
		public const string MutexPath = "Global\\aspnetcore.mutex.lock";
	
		private readonly Mutex mutex;
		public NamedMutex()
		{
			mutex = new Mutex(true, MutexPath);
		}
		public void Dispose()
		{
			mutex.ReleaseMutex();
			mutex.Dispose();
		}
	}
	[JsonIgnore]
	public string ConfigPath => Environment.OSVersion.Platform == PlatformID.Win32NT ?
		Path.Combine(Environment.CurrentDirectory, "applications.json") :
		"/etc/aspnetcore/applications.json";
	[JsonIgnore]
	public ILogger<Worker> Logger = null;
	public List<Application> Applications { get; set; } = new List<Application>();

	bool recursive = false;
	public void Load()
	{
		if (recursive) return;
		recursive = true;

		using (var mutex = new NamedMutex())
		{
			try
			{
				Configuration? config = null;
				if (File.Exists(ConfigPath))
				{
					// Read the configuration file if it exists.
					var txt = File.ReadAllText(ConfigPath);
					config = JsonConvert.DeserializeObject<Configuration>(txt);
				} else
				{
					config = new Configuration();
				}

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

				if (!File.Exists(ConfigPath)) Save();
				else SaveIfDirty();
			}
			finally
			{
				recursive = false;
			}
		}
	}

	public void Listen(Application app)
	{
		var server = new Server(app);
		Task.Run(() => server.ListenAsync());
	}
	public void Save() 
	{
		using (var mutex = new NamedMutex())
		{
			var txt = JsonConvert.SerializeObject(this, Formatting.Indented);
			File.WriteAllText(ConfigPath, txt);
			foreach (var app in Applications) app.Dirty = false;
		}
	}
	public void SaveIfDirty() 
	{
		if (Applications.Any(app => app.Dirty))
		{
			Save();
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
					if (oapp.Server != null)
					{
						oapp.Server.Shutdown();
						oapp.Server = null;
					}
					Applications.Remove(oapp);
					Applications.Add(app);
					Listen(app);
				}
				else
				{
					app.Server = oapp.Server;
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
			Save();
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
				if (app.Server != null)
				{
					app.Server.Shutdown();
					app.Server = null;
				}
				Applications.Remove(app);
				Save();
			}
		}
	}
	public void Update(Application app) => Add(app);
	public void Remove(Application app) => Remove(app.Name);

	public static Configuration Current = new Configuration();

	FileSystemWatcher? watcher = null;
	public void Watch()
	{
		watcher = new FileSystemWatcher(Path.GetDirectoryName(ConfigPath) ?? string.Empty, Path.GetFileName(ConfigPath));
		watcher.Changed += (s, e) => Load();
		watcher.EnableRaisingEvents = true;
		Load();
	}

	public void Shutdown()
	{ 
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
	public string Urls { get; set; }
	public Dictionary<string, string> Environment { get; set; } = new Dictionary<string, string>();
	public string Assembly { get; set; }
	public string Arguments { get; set; }
	public TimeSpan IdleTimeout { get; set; } = Server.GlobalIdleTimeout;
	public TimeSpan Recycle { get; set; } = Server.GlobalRecycle;
	[JsonIgnore]
	public Server Server { get; set; } = null;
}
