# AspNetCoreSharedServer.Middleware

Middleware for ASP.NET Core applications that run behind
[AspNetCoreSharedServer](https://github.com/simonegli8/AspNetCoreSharedServer).

## UseProxyHttp3

AspNetCoreSharedServer can forward HTTP/3 (QUIC over UDP) to your application. Clients learn that HTTP/3 is available from the
`Alt-Svc` header of a normal HTTP/1.1 or HTTP/2 response. Kestrel only knows its own internal port, so on its own it would
advertise a port that clients cannot reach. AspNetCoreSharedServer therefore passes the correct header value, for example
`h3=":443"; ma=86400`, to the application in the environment variable `ALT_SVC_HEADER`. `UseProxyHttp3` sends it:

```
dotnet add package AspNetCoreSharedServer.Middleware
```

```csharp
var app = builder.Build();

app.UseProxyHttp3();
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
  or a PROXY header enabled), the variable is empty and `UseProxyHttp3` adds nothing, so it is safe to leave in.
- The header is set with a plain assignment, so there is never a second `Alt-Svc` header.

See the [AspNetCoreSharedServer documentation](https://github.com/simonegli8/AspNetCoreSharedServer#http3) for how HTTP/3
is forwarded.
