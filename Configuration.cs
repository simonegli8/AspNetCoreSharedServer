using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using Newtonsoft.Json;

namespace AspNetCoreServer;

public class Configuration
{
	public string ConfigPath
	{
		get
		{
			var path = Path.Combine(Environment.CurrentDirectory, "applications.json");
			if (File.Exists(path)) return path;
			path = Path.Combine("etc", "aspnetcore", "applications.json");
			return path;
		}
	}
	public string MutexPath => Path.Combine("etc", "aspnetcore", "mutex", ".lock");
	public Application[] Applications { get; set; } = Array.Empty<Application>();

	bool recursive = false;
	public void Load()
	{
		if (recursive) return;
		recursive = true;

		var dir = Path.GetDirectoryName(MutexPath) ?? string.Empty;
		if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

		var mutex = new Mutex(true, MutexPath);
		try
		{
			var txt = File.ReadAllText(ConfigPath);
			var config = JsonConvert.DeserializeObject<Configuration>(txt);
			var oldApps = Applications
				.OrderBy(app => app.Name)
				.ToArray();
			var newApps = (config?.Applications ?? Array.Empty<Application>())
				.OrderBy(app => app.Name)
				.ToArray();
			int i = 0, oi = 0;
			for (i = 0; i < newApps.Length; i++)
			{
				var app = newApps[i];
				if (oi < oldApps.Length && string.Compare(oldApps[oi].Name, app.Name) < 0)
				{
					var oapp = oldApps[oi];
					if (app.Server != null)
					{
						app.Server.Shutdown();
						app.Server = null;
					}
					oi++;
				}
				if (oi < oldApps.Length && oldApps[oi].Name == app.Name)
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
				}
				else
				{
					Listen(app);
				}
			}
			Applications = newApps;
			SaveIfDirty();
		} finally
		{
			mutex.ReleaseMutex();
			mutex.Dispose();
			recursive = false;
		}
	}

	public void Listen(Application app)
	{
		var server = new Server(app);
		server.Listen();
	}
	public void Save() 
	{
		var dir = Path.GetDirectoryName(MutexPath) ?? string.Empty;
		if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

		var mutex = new Mutex(true, MutexPath);
		try
		{
			var txt = JsonConvert.SerializeObject(this, Formatting.Indented);
			File.WriteAllText(ConfigPath, txt);
		} finally
		{
			mutex.ReleaseMutex();
			mutex.Dispose();
		}
	}
	public void SaveIfDirty() 
	{
		if (Applications.Any(app => app.Dirty))
		{
			Save();
			foreach (var app in Applications) app.Dirty = false;
		}
	}

	public static Configuration Current = new Configuration();

	FileSystemWatcher? watcher = null;
	public void Watch()
	{
		watcher = new FileSystemWatcher(Path.GetDirectoryName(ConfigPath) ?? string.Empty, Path.GetFileName(ConfigPath));
		watcher.Changed += (s, e) => Load();
		watcher.EnableRaisingEvents = true;
		Load();
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
	public Dictionary<string, string> Environment { get; set; }
	public string Assembly { get; set; }
	public string Arguments { get; set; }
	public TimeSpan IdleTimeout { get; set; } = Server.GlobalIdleTimeout;
	public TimeSpan Recycle { get; set; } = Server.GlobalRecycle;
	[JsonIgnore]
	public Server Server { get; set; } = null;
}
