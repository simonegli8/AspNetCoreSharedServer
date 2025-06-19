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
      "Name": "MyApp", // Name of the application
      "Assembly": "Path/To/ASPNETCoreApp.dll", // Path to the ASP.NET Core application DLL
      "Arguments": "", // Optional arguments to pass to the application
      "Urls": "http://original-domain.org", // The original URLs Apache or Nginx will listen on
      "ListenUrls": "http://localhost", // The URLs AspNetCoreSharedServer will listen on, and that you will proxy to from Apache or Nginx
      "IdleTimeout": 300, // Time in seconds after which the application will be stopped if idle
      "Recycle"": 1200, // Time in seconds after which the application will be recycled
      "Environment": {  // Environment variables to set for the application
        "ASPNETCORE_ENVIRONMENT": "Production"
      }
    }
  ]
}
```

After you have defined your applications in the applications.json file, you can proxy to the sockets specified in ListenUrls 
from Apache or Nginx. The original Urls that Apache or Nginx serve will be passed to Kestrel as a environment varibale
ORIGINAL_URLS from the Urls parameter in application.json.