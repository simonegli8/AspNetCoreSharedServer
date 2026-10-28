# AspNetCoreSharedServer

[![NuGet](https://img.shields.io/nuget/v/AspNetCoreSharedServer.svg)](https://www.nuget.org/packages/AspNetCoreSharedServer)

An ASP.NET Core server for Linux & macOS that starts Kestrel on demand and stops it when idle. For use when hosting
ASP.NET Core in a shared hosting environment behind a proxy like Apache or Nginx, where Kestrel should not 
run as a permanent Systemd service using resources, but should be started on demand and shutdown when idle. 

# Usage
First install AspNetCoreSharedServer using the comman `sudo dotnet tool install AspNetCoreSharedServer --global`.
Next, execute `sudo aspnet-server install` to install the server as a service. You might need to add the
`/root/.dontet/tools` path to your PATH environment variable for root first for this to work. This works on Linux using
Systemd or OpenRC (like on Alpine) and on macOS. You can then manage the service with your system's service
commands and the name of the service `aspnet-server`. To uninstall the system service run `sudo aspnet-server uninstall`.
To show help on usage, execute `sudo aspnet-server -?` or `sudo aspnet-server -help`.


## Configure AspNetCoreSharedServer
All the configuration is stored in `/etc/aspnet-server/configuration.json` and in json files named after the application
name in `/etc/aspnet-server`. When the configuration is changed, changes are applied on the fly. The
`configuration.json` file is defined as follows:
```json
{
  "IdleTimeout": 300,
  "Recycle": 1200,
  "Offline": false,
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
      "WorkingDirectory": "/Path/To",
      "Arguments": "",
      "Urls": "http://original-domain.org",
      "ListenUrls": "http://localhost:10000",
      "Environment": {
        "ASPNETCORE_ENVIRONMENT": "Production"
      }
      "IdleTimeout": 300,
      "Recycle": 1200,
      "Offline": false,
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
- `WorkingDirectory` an optional working directory for the Kestrel process. If not specified, the path of `Assembly` will be used.
- `Arguments` are optional command line arguments passed to the application.
- `Urls` is the original URL that the application will respond to. This are the original urls the main server (Apache or NGINX) is
  listening on. This value will be passed to the Kestrel process in the environment variable ORIGINAL_URLS.
- `ListenUrls` are the URLs that this Application will listen on. This is where the proxy server (Apache or NGINX) can forward
  requests to. You can omit the port in this urls and AspNetCoreSharedServer will automatically assign a port.
- `IdleTimeout` is the time in seconds or JSON time value after which an application will be stopped when it is idle.
- `Recycle` is the time in seconds or JSON time value after which the application will be restarted, regardless of activity.
- `Environment` is a dictionary of environment variables that will be set for the application when it is started.
- `Offline` if set to true, the application or the server will be disabled and set offline.
- `Syslog` optional Syslog configuration.
- `FailureLimit` number of failures within `FailureInterval` in order to disable app pool.
- `FailureInterval` time span within to count the number of failures.
- `MemoryLowThreshold` a double value between 0 and 1 indicating the percentage of occupied memory when `IdleTimeout`
  should switch to the `IdleTimeoutOnLowMemory` value. So you can lower `IdleTimeout` when memory is low, so the server
  uses fewer RAM.

The individual applications can also be placed in separate json files, named after the applications name like so:
```
{
  "Name": "MyApp",
  "Assembly": "/Path/To/ASPNETCoreApp.dll",
  "WorkingDirectory": "/Path/To",
  "Arguments": "",
  "Urls": "http://original-domain.org",
  "ListenUrls": "http://localhost:10000",
  "Environment": {
    "ASPNETCORE_ENVIRONMENT": "Production"
  }
  "IdleTimeout": 300,
  "Recycle": 1200,
  "Offline": false,
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

### HTTP/3
For an application with an `https` URL in `ListenUrls`, AspNetCoreSharedServer also listens on the same port for UDP and forwards
HTTP/3 (QUIC) to the application. The application has to do two things:

1. Kestrel must listen on the HTTPS port it is given in `ASPNETCORE_URLS` with `HttpProtocols.Http1AndHttp2AndHttp3`.
2. The application must tell clients where to find HTTP/3. Kestrel only knows its own internal port, so on its own it would
   advertise a port that clients cannot reach. AspNetCoreSharedServer therefore passes the correct `Alt-Svc` header value, for
   example `h3=":443"; ma=86400`, in the environment variable `HTTP3_ALT_SVC_HEADER`. The NuGet package
   `AspNetCoreSharedServer.Middleware` sends it for you with one line:

   ```csharp
   app.UseAspNetCoreSharedServer();
   ```

   Without the package, send it yourself with every response:

   ```csharp
   var altSvc = Environment.GetEnvironmentVariable("HTTP3_ALT_SVC_HEADER");
   if (!string.IsNullOrEmpty(altSvc))
   {
       app.Use(async (context, next) =>
       {
           context.Response.Headers["Alt-Svc"] = altSvc;
           await next();
       });
   }
   ```

   `HTTP3_ALT_SVC_HEADER` is empty, and `HTTP3_ENABLED` is `false`, when HTTP/3 is not forwarded for the application.

Things to know:
- A UDP datagram never starts an application. Without a running application HTTP/3 requests are dropped, and clients fall back to
  TCP, which starts it.
- HTTP/3 traffic counts as activity for `IdleTimeout`.
- The PROXY protocol only exists for TCP. Over HTTP/3 the application would see `127.0.0.1` as the client, so HTTP/3 is switched
  off for applications that have `EnableProxyHeader` or `EnableProxyV2Header` set.
- Every client gets its own session, which ends after 60 seconds without traffic. At most 4096 sessions are kept, and a new
  session only starts with a datagram of at least 1200 bytes, the size of a QUIC client's first packet.
- Set `"EnableHttp3": false` in `configuration.json`, or for a single application, to switch HTTP/3 off.

### PROXY protocol
With `EnableProxyHeader` or `EnableProxyV2Header`, AspNetCoreSharedServer starts every TCP connection to the application with a
PROXY header that carries the real address of the client. Kestrel does not read that header by itself, so the application has to
be told to. Add the NuGet package `AspNetCoreSharedServer.Middleware` and call `UseAspNetCoreSharedServer` on the web host, **before**
the Kestrel endpoints are created:

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseAspNetCoreSharedServer();   // reads the PROXY header; must come before UseKestrel / ConfigureKestrel
builder.WebHost.UseKestrel(options => { ... });

var app = builder.Build();
app.UseAspNetCoreSharedServer();               // sends the Alt-Svc header, see HTTP/3 above
```

- Without this call Kestrel does not understand the header: the application then sees the connection as invalid, and
  `HttpContext.Connection.RemoteIpAddress` is not the client.
- The call does nothing unless the server sets the environment variable `PROXY_PROTOCOL` (`v1` or `v2`), so it is safe to leave in
  when the application also runs without AspNetCoreSharedServer or without a PROXY header.
- It turns on [Scintillating.ProxyProtocol.Middleware](https://www.nuget.org/packages/Scintillating.ProxyProtocol.Middleware) (a
  dependency of the package, .NET 8 or newer) for every Kestrel endpoint, and it also works together with `UseHttps`.
- When `PROXY_PROTOCOL` is set, every connection must carry the header. That is always the case behind AspNetCoreSharedServer, but
  it means the same port cannot also serve direct connections.
- HTTP/3 is switched off for an application that gets a PROXY header, because the PROXY protocol only exists for TCP.

# API Library
You can also manage the configuration.json file programmatically by using AspNetCoreSharedServer.Api:
Import AspNetCoreSharedServer.Api via nuget to your project.

```
using AspNetCoreSharedServer;
...
var app = new Application()
{
  Name = "Name of the ASP.NET Core application",
  Assembly = "/Path/To/StartupAssembly.dll",
  WorkingDirectory = "Path/To",
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
or 
await AspServer.Configuration.UpdateAsync(app);
or
await AspServer.Configuration.AddAsync(app);
or
await AspServer.Configuration.RemoveAsync(app);
or
await AspServer.Configuration.RemoveAsync("App Name");
```

Or to lookup an application:

```
using AspNetCoreSharedServer;
...
AspServer.Configuration.Load();
or
await AspServer.Configuration.LoadAsync();

var app = AspServer.Configuration.Applications["Name of Application"];
...
```

To find a free IP port:
```
var port = AspServer.FindFreePort();
or
var port = await AspServer.FindFreePortAsync();
```

If you do non atomic stuff with AspServer.Configuration, you must enclose it in a mutex lock, so always only
one process can modify `configuration.json`:

```
using (var mutex = AspServer.Lock()) {
    AspServer.Configuration.Load();

    code that manipulates AspServer.Configuration ...
    
    AspServer.Configuration.Save();
}

or

using (var mutex = await AspServer.LockAsync()) {
    await AspServer.Configuration.LoadAsync();

    code that manipulates AspServer.Configuration ...
    
    await AspServer.Configuration.SaveAsync();
}
```