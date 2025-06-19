# AspNetCoreSharedServer
A ASP.NET Core server for Linux that starts Kestrel on demand and stops it when idle. For use when hosting
ASP.NET Core in a shared hosting environment behind a proxy like Apache or Nginx, where Kestrel should not 
run as a permanent Systemd service using resources, but should be started on demand and shutdown when idle. 

# Usage
Run AspNetCoreSharedServer as a Systemd service. All the configuration is stored in /etc/aspnetcore/applications.json.
When the configuration is changed, changes are applied on the fly. The applications.json file is defined as follows:
```json
{
  "Applications": [
    {
      "Name": "MyApp",
      "Assembly": "Path/To/ASPNETCoreApp.dll",
      "Arguments": "",
      "Urls": "http://original-domain.org",
      "ListenUrls": "http://localhost",
      "IdleTimeout": 300,
      "Recycle": 1200,
      "Environment": {
        "ASPNETCORE_ENVIRONMENT": "Production"
      }
    }
  ]
}
```
- `Name` specifies the name of the application. It must be unique.
- `Assembly` denotes the path to the ASP.NET Core application DLL.
- `Arguments` are optional command line arguments passed to the application.
- `Urls` is the original URL that the application will respond to. This are the urls as received by the proxy server.
- `ListenUrls` are the URLs that this Application will listen on. This is where the proxy server will forward requests to.
- `IdleTimeout` is the time in seconds after which the application will be stopped when it is idle.
- `Recycle` is the time in seconds after which the application will be restarted, regardless of activity.
- `Environment` is a dictionary of environment variables that will be set for the application when it is started.

After you have defined your applications in the applications.json file, you can proxy to the sockets specified in ListenUrls 
from Apache or Nginx. The original Urls that Apache or Nginx serve will be passed to Kestrel as a environment varibale
ORIGINAL_URLS from the Urls parameter in applications.json.