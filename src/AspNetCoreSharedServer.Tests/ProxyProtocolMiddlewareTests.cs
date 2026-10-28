using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AspNetCoreSharedServer.Tests;

/// <summary>
/// UseAspNetCoreSharedServer on the web host builder: a real Kestrel on loopback that is given PROXY headers over a raw socket,
/// the way AspNetCoreSharedServer sends them.
/// </summary>
public class ProxyProtocolMiddlewareTests
{
    static readonly byte[] V2Signature = { 0x0D, 0x0A, 0x0D, 0x0A, 0x00, 0x0D, 0x0A, 0x51, 0x55, 0x49, 0x54, 0x0A };

    static byte[] V1Header() => Encoding.ASCII.GetBytes("PROXY TCP4 203.0.113.7 192.0.2.1 5555 443\r\n");

    static byte[] V2Header()
    {
        var header = new List<byte>(V2Signature) { 0x21, 0x11, 0x00, 0x0C };
        header.AddRange(IPAddress.Parse("203.0.113.7").GetAddressBytes());
        header.AddRange(IPAddress.Parse("192.0.2.1").GetAddressBytes());
        header.AddRange(new byte[] { 0x15, 0xB3, 0x01, 0xBB }); // 5555 -> 443
        return header.ToArray();
    }

    /// <summary>Starts Kestrel on a free loopback port; the app answers with the client address it sees.</summary>
    static async Task<(WebApplication App, int Port)> StartAsync(string? proxyProtocol)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseAspNetCoreSharedServer(proxyProtocol);
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        app.MapGet("/", (HttpContext context) => context.Connection.RemoteIpAddress?.ToString() + ":" + context.Connection.RemotePort);
        await app.StartAsync();
        var address = new Uri(app.Urls.First());
        return (app, address.Port);
    }

    /// <summary>Sends the bytes, then a plain GET, and returns everything the server answers.</summary>
    static async Task<string> RequestAsync(int port, byte[]? header)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var stream = client.GetStream();
        var request = Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n");
        if (header != null) await stream.WriteAsync(header);
        await stream.WriteAsync(request);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var buffer = new MemoryStream();
        try { await stream.CopyToAsync(buffer, timeout.Token); } catch (IOException) { }
        return Encoding.ASCII.GetString(buffer.ToArray());
    }

    [Theory]
    [InlineData("v1")]
    [InlineData("v2")]
    public async Task Kestrel_sees_the_client_address_from_the_header(string version)
    {
        var (app, port) = await StartAsync(version);
        try
        {
            var response = await RequestAsync(port, version == "v1" ? V1Header() : V2Header());
            Assert.Contains("200 OK", response);
            Assert.Contains("203.0.113.7:5555", response); // the body is chunked, so it is not at the very end
        }
        finally { await app.StopAsync(); await app.DisposeAsync(); }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("v3")]
    public async Task Without_the_variable_nothing_changes(string? value)
    {
        var (app, port) = await StartAsync(value);
        try
        {
            var plain = await RequestAsync(port, null);
            Assert.Contains("200 OK", plain);
            Assert.Contains("127.0.0.1:", plain);

            // A PROXY header is not understood, so it is a bad request: nothing silently believes a header it was not told to expect.
            var withHeader = await RequestAsync(port, V1Header());
            Assert.DoesNotContain("203.0.113.7", withHeader);
        }
        finally { await app.StopAsync(); await app.DisposeAsync(); }
    }

    [Fact]
    public async Task With_the_variable_a_connection_without_header_is_refused()
    {
        var (app, port) = await StartAsync("v1");
        try
        {
            var response = await RequestAsync(port, null);
            Assert.DoesNotContain("200 OK", response);
        }
        finally { await app.StopAsync(); await app.DisposeAsync(); }
    }

    [Fact]
    public void The_variable_name_is_the_one_the_server_sets()
    {
        Assert.Equal(Server.ProxyProtocolVariable, AspNetCoreSharedServerExtensions.ProxyProtocolVariable);
    }

    [Fact]
    public void Rejects_a_null_builder()
    {
        Assert.Throws<ArgumentNullException>(() => AspNetCoreSharedServerExtensions.UseAspNetCoreSharedServer((IWebHostBuilder)null!));
    }
}
