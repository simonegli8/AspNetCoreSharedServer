# AspNetCoreSharedServer.Middleware

Middleware for ASP.NET Core applications that run behind
[AspNetCoreSharedServer](https://github.com/simonegli8/AspNetCoreSharedServer).

## UseAspNetCoreSharedServer

One method name, two places to call it: on the application (`app`) for HTTP/3, and on the web host (`builder.WebHost`) for the
PROXY protocol. Both do nothing outside AspNetCoreSharedServer.

### HTTP/3 (`app.UseAspNetCoreSharedServer()`)

AspNetCoreSharedServer can forward HTTP/3 (QUIC over UDP) to your application. Clients learn that HTTP/3 is available from the
`Alt-Svc` header of a normal HTTP/1.1 or HTTP/2 response. Kestrel only knows its own internal port, so on its own it would
advertise a port that clients cannot reach. AspNetCoreSharedServer therefore passes the correct header value, for example
`h3=":443"; ma=86400`, to the application in the environment variable `HTTP3_ALT_SVC_HEADER`. `UseAspNetCoreSharedServer` sends it:

```
dotnet add package AspNetCoreSharedServer.Middleware
```

```csharp
var app = builder.Build();

app.UseAspNetCoreSharedServer();
```

That is all. Kestrel must also listen with HTTP/3 enabled on the HTTPS port it is given in `ASPNETCORE_URLS`:

```csharp
builder.WebHost.UseKestrel(options =>
{
    options.ListenAnyIP(httpsPort, listenOptions =>
    {
        listenOptions.UseHttps();
        listenOptions.Protocols = HttpProtocols.Http1AndHttp2AndHttp3;
    });
});
```

- The value is read once, when the pipeline is built.
- Outside AspNetCoreSharedServer, or when it does not forward HTTP/3 for the application (HTTP/3 switched off, no `https` URL,
  or a PROXY header enabled), the variable is empty and `UseAspNetCoreSharedServer` adds nothing, so it is safe to leave in.
- The header is set with a plain assignment, so there is never a second `Alt-Svc` header.

### PROXY protocol (`builder.WebHost.UseAspNetCoreSharedServer()`)

With `EnableProxyHeader` or `EnableProxyV2Header`, AspNetCoreSharedServer starts every TCP connection to your application with a
PROXY header, so that the application can see the real address of the client, and sets the environment variable
`PROXY_PROTOCOL` to `v1` or `v2`. When that variable is set, `UseAspNetCoreSharedServer` on the web host turns on
[Scintillating.ProxyProtocol.Middleware](https://www.nuget.org/packages/Scintillating.ProxyProtocol.Middleware) (a dependency of
this package) for every Kestrel endpoint. `HttpContext.Connection.RemoteIpAddress` is then the real client.

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseAspNetCoreSharedServer();   // before UseKestrel / ConfigureKestrel
builder.WebHost.UseKestrel(options => { ... });
```

- Call it **before** the Kestrel endpoints are created: it applies to the endpoints that are created after it.
- The header is read before TLS, so it works together with `UseHttps`.
- When the variable is not set, nothing changes. When it is set, every connection must carry the header; Kestrel refuses the
  others. Behind AspNetCoreSharedServer that is always the case, but a port cannot serve both kinds of connection.
- HTTP/3 is not forwarded when a PROXY header is enabled, because the PROXY protocol only exists for TCP.
- This part needs .NET 8 or newer (a requirement of Scintillating.ProxyProtocol.Middleware).

See the [AspNetCoreSharedServer documentation](https://github.com/simonegli8/AspNetCoreSharedServer#http3) for how HTTP/3
is forwarded.
