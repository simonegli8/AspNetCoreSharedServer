# AspNetCoreSharedServer
A ASP.NET Core server for Linux that starts Kestrel on demand and stops it when idle. For use when hosting
ASP.NET Core in a shared hosting environment behind a proxy like Apache or Nginx, where Kestrel should not 
run as a permanent Systemd service using resources, but should be started on demand and shutdown when idle. 

# Usage
First install AspNetCoreSharedServer using the comman `sudo dotnet tool install AspNetCoreSharedServer --global`.

## Run AspNetCoreSharedServer as a Systemd service.
Next, use a aspnetcore-shared-server.service file like the following to run AspNetCoreSharedServer as a Systemd service:
```ini
[Unit]
Description=AspNetCoreSharedServer service, a shared server for serving ASP.NET Core applications over a proxy.
Requires=network-online.target
After=network-online.target
StartLimitIntervalSec=500
StartLimitBurst=5

[Service]
Type=simple
ExecStart=/root/.dotnet/tools/AspNetCoreSharedServer
Environment="ASPNETCORE_ENVIRONMENT=Production"
Environment="DOTNET_ROOT=/usr/share/dotnet"
Restart=on-failure
RestartSec=1s
StandardOutput=journal+console
StandardError=journal+console
SyslogIdentifier=aspnetcore-shared-server
User=root
Group=www-data

[Install]
WantedBy=multi-user.target
```

Put the service file in `/lib/systemd/system/aspnetcore-shared-server.service` and enable it with:
```bash
sudo systemctl enable aspnetcore-shared-server
sudo systemctl start aspnetcore-shared-server
```

## Configure AspNetCoreSharedServer
All the configuration is stored in /etc/aspnetcore/applications.json.
When the configuration is changed, changes are applied on the fly. The applications.json file is defined as follows:
```json
{
  "IdleTimeout": 300,
  "Recycle": 1200,
  "Applications": [
    {
      "Name": "MyApp",
      "Assembly": "/Path/To/ASPNETCoreApp.dll",
      "Arguments": "",
      "Urls": "http://original-domain.org",
      "ListenUrls": "http://localhost:10000",
      "Environment": {
        "ASPNETCORE_ENVIRONMENT": "Production"
      }
      "IdleTimeout": 300,
      "Recycle": 1200
    }, 
    ... more applications can be defined here ...
  ]
}
```
- `Name` specifies the name of the application. It must be unique.
- `Assembly` denotes the path to the ASP.NET Core application DLL.
- `Arguments` are optional command line arguments passed to the application.
- `Urls` is the original URL that the application will respond to. This are the urls as received by the proxy server.
- `ListenUrls` are the URLs that this Application will listen on. This is where the proxy server will forward requests to.
  You can omit the port in this urls and AspNetCoreSharedServer will automatically assign a port.
- `IdleTimeout` is the time in seconds or JSON time value after which the application will be stopped when it is idle.
- `Recycle` is the time in seconds or JSON time value after which the application will be restarted, regardless of activity.
- `Environment` is a dictionary of environment variables that will be set for the application when it is started.

After you have defined your applications in the applications.json file, you can proxy to the sockets specified in ListenUrls 
from Apache or Nginx. The original Urls that Apache or Nginx serve will be passed to Kestrel as a environment varibale
ORIGINAL_URLS from the Urls parameter in applications.json.

You can also manage the applications.json file programmatically by using AspNetCoreSharedServer.Api:
Import AspNetCoreSharedServer.Api via nuget to your project.

```
using AspNetCoreSharedServer;
...
var app = new Application()
{
	Name = "Name of the ASP.NET Core application",
	Assembly = "Startup Assembly",
	Arguments = "Startup Arguments",
	Environment = new Dictionary<string, string>()
	{
		{ "ASPNETCORE_ENVIRONMENT", "Production" }
	},
	ListenUrls = "http://localhost:10000",
	Urls = "http://original-domain.org",
};
Configuration.Current.Update(app);
or
Configuration.Current.Add(app);
or
Configuration.Current.Remove(app);
or
Configuration.Current.Remove("App Name");
```

Or to lookup an application:

```
using AspNetCoreSharedServer;
...
Configuration.Current.Load();
var app = Configuration.Current.Applications.FirstOrDefault(app => app.Name == "Name of Application");
...
```

To Find a free IP port:
```
var port = Configuration.Current.FindFreePort();
```