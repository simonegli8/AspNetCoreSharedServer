using Microsoft.AspNetCore.Http;

// In the namespace of the other Use... methods, so that no extra using is needed in Program.cs.
namespace Microsoft.AspNetCore.Builder;

/// <summary>
/// Lets an application that runs behind AspNetCoreSharedServer advertise HTTP/3 on the public port of the server.
/// </summary>
public static class ProxyHttp3Extensions
{
    /// <summary>
    /// The environment variable in which AspNetCoreSharedServer passes the value of the <c>Alt-Svc</c> header to the application,
    /// for example <c>h3=":443"; ma=86400</c>. It is empty when HTTP/3 is not forwarded for the application.
    /// </summary>
    public const string AltSvcVariable = "ALT_SVC_HEADER";

    /// <summary>
    /// Sends the <c>Alt-Svc</c> header that AspNetCoreSharedServer provides with every response, so that clients find HTTP/3 on
    /// the public port of the server. Kestrel only knows its own internal port, so on its own it would advertise a port that
    /// clients cannot reach.
    /// </summary>
    /// <remarks>
    /// The value is read once, when the pipeline is built. Without AspNetCoreSharedServer, or when it does not forward HTTP/3
    /// for the application, the environment variable is empty and no middleware is added: the call then costs nothing.
    /// </remarks>
    public static IApplicationBuilder UseProxyHttp3(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseProxyHttp3(Environment.GetEnvironmentVariable(AltSvcVariable));
    }

    /// <summary>The same with an explicit header value (null or empty adds nothing). Used by the tests.</summary>
    internal static IApplicationBuilder UseProxyHttp3(this IApplicationBuilder app, string? altSvc)
    {
        if (string.IsNullOrEmpty(altSvc)) return app;

        return app.Use(next => context =>
        {
            // A plain assignment replaces any value that is already there, so there is never a second Alt-Svc header.
            context.Response.Headers["Alt-Svc"] = altSvc;
            return next(context);
        });
    }
}
