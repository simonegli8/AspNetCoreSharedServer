# AspNetCoreSharedServer
A ASP.NET Core server for Linux & macOS that starts Kestrel on demand and stops it when idle. For use when hosting
ASP.NET Core in a shared hosting environment behind a proxy like Apache or Nginx, where Kestrel should not 
run as a permanent Systemd service using resources, but should be started on demand and shutdown when idle. 

# Usage
First install AspNetCoreSharedServer using the comman `sudo dotnet tool install AspNetCoreSharedServer --global`.
Next, execute `aspnet-server install` to install the server as a service. This works on Linux using Systemd or OpenRC
(like on Alpine) and on macOS. You can then manage the service with your system's service commands and the name
`aspnet-server`. To uninstall the system service run `aspnet-server uninstall`. To show help on usage, execute 
`aspnet-server -?` or `aspnet-server -help`.


## Configure AspNetCoreSharedServer
All the configuration is stored in `/etc/aspnet-server/configuration.json` a and in json files named after the application
name in `/etc/aspnet-server`. When the configuration is changed, changes are applied on the fly. The
`configuration.json` file is defined as follows:
```json
{
  "IdleTimeout": 300,
  "Recycle": 1200,
  "Disbled": false,
  "User": "www-data",
  "Group": "www-data"
  "FailureLimit": 5,
  "FailureInterval": "00:05:00",
  "MemoryLowThreshold": 0.95,
  "IdleTimeoutOnLowMemory": "00:01:00"
  "Syslog": {
    "Host": "localhost",
    "Port": 514,
    "Protocol": "Udp"
  },
  "Command": "None",

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
      "Recycle": 1200,
      "Disabled": false,
      "User": "www-data",
      "Group": "www-data",
      "Status": "Running"
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
- `Disable` if true, the application or the server is disabled.
- `Syslog` optional Syslog configuration.
- `FailureLimit` number of failures within `FailureInterval` in order to disable app pool.
- `FailureInterval` time span within to count the number of failures.

The individual applications can also be placed in separate json files, named after the applications name like so:
```
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
    "Recycle": 1200,
    "Disabled": false,
    "User": "www-data",
    "Group": "www-data",
    "Status": "Running"
} 
```
This would be placed in `/etc/aspnet-server/MyApp.json`. Note that `aspnet-server install` sets the permissions of
`/etc/aspnet-server` to user only access and the owner & group of  `/etc/aspnet-server` to `root` & `www-data`.
That means that only `root` can create applications. If you want all users of group `www-data` to be
able to create applications, change access to  `/etc/aspnet-server` to user & group.

After you have defined your applications in the configuration.json file, you can proxy to the sockets specified in ListenUrls 
from Apache or Nginx. The original Urls that Apache or Nginx serve will be passed to Kestrel as a environment varibale
ORIGINAL_URLS from the Urls parameter in configuration.json.

You can also manage the configuration.json file programmatically by using AspNetCoreSharedServer.Api:
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
AspServer.Configuration.Update(app);
or
AspServer.Configuration.Add(app);
or
AspServer.Configuration.Remove(app);
or
AspServer.Configuration.Remove("App Name");
```

Or to lookup an application:

```
using AspNetCoreSharedServer;
...
AspServer.Configuration.Load();
var app = AspServer.Configuration.Applications["Name of Application"];
...
```

To find a free IP port:
```
var port = AspServer.FindFreePort();
```

If you do non atomic stuff with AspServer.Configuration, you must enclose it in a mutex lock, so always only
one process can modify `configuration.json`:

```
using (var mutex = AspServer.Mutex) {
    AspServer.Configuration.Load();

    code that manupulates AspServer.Configuration ...
    
    AspServer.Configuration.Save();
}
```