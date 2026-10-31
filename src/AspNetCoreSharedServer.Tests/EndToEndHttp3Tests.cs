using System.Diagnostics;
using System.Net;
using System.Net.Quic;
using System.Security.Cryptography.X509Certificates;
using Xunit;

namespace AspNetCoreSharedServer.Tests;

/// <summary>
/// HTTP/3 through the real server, with the real test app behind it, and a real HTTP/3 client in front. Slow (about a minute each),
/// so they only run when asked: set the environment variable ASPNETSERVER_E2E=1. They need Windows, elevated (see <see cref="ServerHost"/>),
/// QUIC support in the OS, and the ASP.NET Core HTTPS development certificate (<c>dotnet dev-certs https</c>).
/// </summary>
[Trait("Category", "EndToEnd")]
public class EndToEndHttp3Tests
{
    static void RequirePrerequisites()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("ASPNETSERVER_E2E") == "1", "Opt-in: set ASPNETSERVER_E2E=1 to run the end-to-end tests.");
        Skip.IfNot(OperatingSystem.IsWindows(), "Only on Windows can the test tell the server where its configuration is (ASPNETSERVER_CONFIG_DIR); elsewhere it uses /etc/aspnet-server, which a test must not touch.");
        Skip.IfNot(WindowsFileSecurity.IsAdministrator(), "The server only loads a configuration.json that administrators alone can change, so it has to run elevated.");
        Skip.IfNot(QuicConnection.IsSupported, "QUIC is not available on this machine.");
        Skip.IfNot(DevelopmentCertificateExists(), "There is no ASP.NET Core HTTPS development certificate (run: dotnet dev-certs https).");
        Skip.IfNot(File.Exists(TestPaths.ServerDll) && File.Exists(TestPaths.TestAppDll), "The server or the test app is not built.");
    }

    static bool DevelopmentCertificateExists()
    {
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);
        // ASP.NET Core marks its development certificate with this extension.
        return store.Certificates.Any(certificate => certificate.NotAfter > DateTime.Now &&
            certificate.Extensions.Any(extension => extension.Oid?.Value == "1.3.6.1.4.1.311.84.1.1"));
    }

    static HttpClient NewClient(HttpVersionPolicy policy, Version version, int timeoutSeconds) =>
        new(new SocketsHttpHandler { SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true } })
        {
            DefaultRequestVersion = version,
            DefaultVersionPolicy = policy,
            Timeout = TimeSpan.FromSeconds(timeoutSeconds),
        };

    static async Task<string> GetPidAsync(HttpClient client, string url, int expectedMajorVersion)
    {
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expectedMajorVersion, response.Version.Major);
        return await response.Content.ReadAsStringAsync();
    }

    static async Task KillAppAsync(string pid)
    {
        using var app = Process.GetProcessById(int.Parse(pid));
        app.Kill();
        await app.WaitForExitAsync();
        await Task.Delay(1500); // let the server notice that the app is gone
    }

    [SkippableFact]
    public async Task Http3_is_forwarded_discovered_and_works_again_after_the_app_restarts()
    {
        RequirePrerequisites();
        await using var host = await ServerHost.StartAsync(idleTimeout: "00:05:00", recycle: "00:20:00");
        var url = $"https://localhost:{host.Port}/pid";

        // HTTP/3 while no app is running: nobody answers, and the datagrams must not start the app.
        using (var client = NewClient(HttpVersionPolicy.RequestVersionExact, HttpVersion.Version30, 4))
        {
            await Assert.ThrowsAnyAsync<Exception>(() => client.GetAsync(url));
        }
        Assert.Equal(0, host.CountInLog("Starting TestApp"));

        // A TCP request starts the app. The app sends the Alt-Svc header that the server gave it (public port, not Kestrel's own).
        string pid;
        using (var client = NewClient(HttpVersionPolicy.RequestVersionOrHigher, HttpVersion.Version20, 60))
        {
            var response = await client.GetAsync(url);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(2, response.Version.Major);
            pid = await response.Content.ReadAsStringAsync();
            Assert.True(response.Headers.TryGetValues("Alt-Svc", out var altSvc));
            Assert.StartsWith($"h3=\":{host.Port}\"", altSvc!.Single()); // HttpClient prints the default "ma=86400" away

            // The same client discovers HTTP/3 from Alt-Svc and switches by itself, and the same app instance answers.
            var later = response;
            for (int i = 0; i < 12 && later.Version.Major != 3; i++)
            {
                await Task.Delay(300);
                later = await client.GetAsync(url);
            }
            Assert.Equal(3, later.Version.Major);
            Assert.Equal(pid, await later.Content.ReadAsStringAsync());
        }

        // Explicit HTTP/3 and five concurrent HTTP/3 clients (five QUIC connections, five relay sessions).
        using (var client = NewClient(HttpVersionPolicy.RequestVersionExact, HttpVersion.Version30, 15))
        {
            Assert.Equal(pid, await GetPidAsync(client, url, 3));
        }
        var concurrent = await Task.WhenAll(Enumerable.Range(0, 5).Select(async _ =>
        {
            using var client = NewClient(HttpVersionPolicy.RequestVersionExact, HttpVersion.Version30, 15);
            return await GetPidAsync(client, url, 3);
        }));
        Assert.All(concurrent, answer => Assert.Equal(pid, answer));

        // The app dies. UDP must not bring it back; a TCP request does, as a new instance; then HTTP/3 works again.
        await KillAppAsync(pid);
        using (var client = NewClient(HttpVersionPolicy.RequestVersionExact, HttpVersion.Version30, 4))
        {
            await Assert.ThrowsAnyAsync<Exception>(() => client.GetAsync(url));
        }
        Assert.Equal(1, host.CountInLog("Starting TestApp"));

        string newPid;
        using (var client = NewClient(HttpVersionPolicy.RequestVersionOrLower, HttpVersion.Version20, 60))
        {
            newPid = await GetPidAsync(client, url, 2);
        }
        Assert.NotEqual(pid, newPid);
        Assert.Equal(2, host.CountInLog("Starting TestApp"));
        using (var client = NewClient(HttpVersionPolicy.RequestVersionExact, HttpVersion.Version30, 15))
        {
            Assert.Equal(newPid, await GetPidAsync(client, url, 3));
        }
    }

    [SkippableFact]
    public async Task Http3_traffic_counts_as_activity_for_the_idle_timeout()
    {
        RequirePrerequisites();
        await using var host = await ServerHost.StartAsync(idleTimeout: "00:00:06", recycle: "00:20:00");
        var url = $"https://localhost:{host.Port}/pid";

        string pid;
        using (var client = NewClient(HttpVersionPolicy.RequestVersionOrLower, HttpVersion.Version20, 60))
        {
            pid = await GetPidAsync(client, url, 2);
        }

        // Only HTTP/3 from now on, for much longer than the idle timeout: the app must stay.
        for (int i = 0; i < 8; i++)
        {
            await Task.Delay(2000);
            using var client = NewClient(HttpVersionPolicy.RequestVersionExact, HttpVersion.Version30, 10);
            Assert.Equal(pid, await GetPidAsync(client, url, 3));
        }
        Assert.Equal(0, host.CountInLog("Shutdown TestApp"));
        using (var app = Process.GetProcessById(int.Parse(pid)))
        {
            Assert.False(app.HasExited);
        }

        // Without any traffic it is shut down after the idle timeout.
        await host.WaitForLogAsync("Shutdown TestApp", TimeSpan.FromSeconds(30));
    }
}
