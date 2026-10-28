using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Scintillating.ProxyProtocol.Middleware;

// In the namespace of the other Use... methods, so that no extra using is needed in Program.cs.
namespace Microsoft.AspNetCore.Builder;

/// <summary>
/// Lets an application that runs behind AspNetCoreSharedServer advertise HTTP/3 on the public port of the server and read the PROXY
/// protocol header that the server sends.
/// </summary>
public static class AspNetCoreSharedServerExtensions
{
    /// <summary>
    /// The environment variable in which AspNetCoreSharedServer passes the value of the <c>Alt-Svc</c> header to the application,
    /// for example <c>h3=":443"; ma=86400</c>. It is empty when HTTP/3 is not forwarded for the application.
    /// </summary>
    public const string AltSvcVariable = "HTTP3_ALT_SVC_HEADER";

    /// <summary>
    /// The environment variable in which AspNetCoreSharedServer tells the application that every TCP connection starts with a
    /// PROXY protocol header: <c>v1</c> or <c>v2</c>. It is not set when no header is sent.
    /// </summary>
    public const string ProxyProtocolVariable = "PROXY_PROTOCOL";

    extension(IWebHostBuilder builder)
    {
        /// <summary>
        /// Makes Kestrel read the PROXY protocol header that AspNetCoreSharedServer sends (<c>EnableProxyHeader</c> or
        /// <c>EnableProxyV2Header</c>), so that the application sees the real address of the client. Nothing happens when the
        /// environment variable <c>PROXY_PROTOCOL</c> is not set.
        /// </summary>
        /// <remarks>
        /// Call it before <c>UseKestrel</c> / <c>ConfigureKestrel</c> that create endpoints: the setting is applied to the endpoints
        /// that are created after it. The header is read before TLS, so it also works with <c>UseHttps</c>. Every endpoint then
        /// requires the header, which is right behind AspNetCoreSharedServer, where all connections come from the server.
        /// </remarks>
        public IWebHostBuilder UseAspNetCoreSharedServer()
        {
            ArgumentNullException.ThrowIfNull(builder);
            return builder.UseAspNetCoreSharedServer(Environment.GetEnvironmentVariable(ProxyProtocolVariable));
        }

        /// <summary>The same with an explicit value (<c>v1</c> or <c>v2</c>; anything else adds nothing). Used by the tests.</summary>
        internal IWebHostBuilder UseAspNetCoreSharedServer(string? proxyProtocol)
        {
            // The library reads v1 and v2 alike, so the version only decides whether it is switched on.
            if (proxyProtocol is not ("v1" or "v2")) return builder;

            return builder.ConfigureKestrel(options => options.ConfigureEndpointDefaults(listen => listen.UseProxyProtocol()));
        }
        public Version? AspNetCoreSharedServerVersion
        {
            get
            {
                var env = Environment.GetEnvironmentVariable("ASPNET_SERVER");
                if (env == null) return null;
                else return new Version(env);
            }
        }
        public bool IsAspNetCoreSharedServer => get_AspNetCoreSharedServerVersion(builder) != null; 
    }

    extension(IApplicationBuilder app)
    {
        /// <summary>
        /// Sends the <c>Alt-Svc</c> header that AspNetCoreSharedServer provides with every response, so that clients find HTTP/3 on
        /// the public port of the server. Kestrel only knows its own internal port, so on its own it would advertise a port that
        /// clients cannot reach.
        /// </summary>
        /// <remarks>
        /// The value is read once, when the pipeline is built. Without AspNetCoreSharedServer, or when it does not forward HTTP/3
        /// for the application, the environment variable is empty and no middleware is added: the call then costs nothing.
        /// </remarks>
        public IApplicationBuilder UseAspNetCoreSharedServer()
        {
            ArgumentNullException.ThrowIfNull(app);
            return app.UseAspNetCoreSharedServer(Environment.GetEnvironmentVariable(AltSvcVariable));
        }

        /// <summary>The same with an explicit header value (null or empty adds nothing). Used by the tests.</summary>
        internal IApplicationBuilder UseAspNetCoreSharedServer(string? altSvc)
        {
            if (string.IsNullOrEmpty(altSvc)) return app;

            return app.Use(next => context =>
            {
                // A plain assignment replaces any value that is already there, so there is never a second Alt-Svc header.
                context.Response.Headers["Alt-Svc"] = altSvc;
                return next(context);
            });
        }
        public Version? AspNetCoreSharedServerVersion
        {
            get
            {
                var env = Environment.GetEnvironmentVariable("ASPNET_SERVER");
                if (env == null) return null;
                else return new Version(env);
            }
        }
        public bool IsAspNetCoreSharedServer => get_AspNetCoreSharedServerVersion(app) != null;

    }
}
