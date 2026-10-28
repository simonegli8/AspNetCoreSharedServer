using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace AspNetCoreSharedServer.Tests;

/// <summary>
/// The PROXY protocol header describes the client's connection to the proxy: the client is the source, and the address and
/// port the client connected to is the destination (not the proxy's own socket to the app).
/// </summary>
public class ProxyHeaderTests
{
    /// <summary>Connects a client to a listener and returns the connection as the proxy accepted it.</summary>
    static async Task<(TcpClient Accepted, TcpClient Client, int ClientPort, int ListenPort)> ConnectAsync(IPAddress listen, IPAddress connectTo, bool dualMode = false)
    {
        var listener = new TcpListener(listen, 0);
        if (dualMode) listener.Server.DualMode = true;
        listener.Start();
        try
        {
            var listenPort = ((IPEndPoint)listener.LocalEndpoint).Port;
            var client = new TcpClient(connectTo.AddressFamily);
            await client.ConnectAsync(connectTo, listenPort);
            var accepted = await listener.AcceptTcpClientAsync();
            return (accepted, client, ((IPEndPoint)client.Client.LocalEndPoint!).Port, listenPort);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task V1_for_IPv4_names_the_port_the_client_connected_to()
    {
        var (accepted, client, clientPort, listenPort) = await ConnectAsync(IPAddress.Loopback, IPAddress.Loopback);
        using (accepted) using (client)
        {
            var header = Encoding.ASCII.GetString(Server.CreateProxyHeader(accepted, false));

            Assert.Equal($"PROXY TCP4 127.0.0.1 127.0.0.1 {clientPort} {listenPort}\r\n", header);
        }
    }

    [SkippableFact]
    public async Task V1_for_IPv6()
    {
        Skip.IfNot(NetHelpers.IPv6Available, "This machine has no IPv6 loopback.");
        var (accepted, client, clientPort, listenPort) = await ConnectAsync(IPAddress.IPv6Loopback, IPAddress.IPv6Loopback);
        using (accepted) using (client)
        {
            var header = Encoding.ASCII.GetString(Server.CreateProxyHeader(accepted, false));

            Assert.Equal($"PROXY TCP6 ::1 ::1 {clientPort} {listenPort}\r\n", header);
        }
    }

    [SkippableFact]
    public async Task V1_unwraps_IPv4_addresses_of_a_dual_mode_listener()
    {
        Skip.IfNot(NetHelpers.IPv6Available, "This machine has no IPv6 loopback.");
        var (accepted, client, clientPort, listenPort) = await ConnectAsync(IPAddress.IPv6Any, IPAddress.Loopback, dualMode: true);
        using (accepted) using (client)
        {
            // The socket reports IPv4-mapped addresses (::ffff:127.0.0.1), which must not leak into the header.
            Assert.True(((IPEndPoint)accepted.Client.LocalEndPoint!).Address.IsIPv4MappedToIPv6);

            var header = Encoding.ASCII.GetString(Server.CreateProxyHeader(accepted, false));

            Assert.Equal($"PROXY TCP4 127.0.0.1 127.0.0.1 {clientPort} {listenPort}\r\n", header);
        }
    }

    [Fact]
    public async Task V2_for_IPv4()
    {
        var (accepted, client, clientPort, listenPort) = await ConnectAsync(IPAddress.Loopback, IPAddress.Loopback);
        using (accepted) using (client)
        {
            var header = Server.CreateProxyHeader(accepted, true);

            byte[] signature = { 0x0D, 0x0A, 0x0D, 0x0A, 0x00, 0x0D, 0x0A, 0x51, 0x55, 0x49, 0x54, 0x0A };
            Assert.Equal(28, header.Length);
            Assert.True(header.AsSpan(0, 12).SequenceEqual(signature));
            Assert.Equal(0x21, header[12]); // version 2, PROXY command
            Assert.Equal(0x11, header[13]); // TCP over IPv4
            Assert.Equal(12, BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(14, 2)));
            Assert.Equal(IPAddress.Loopback, new IPAddress(header.AsSpan(16, 4)));
            Assert.Equal(IPAddress.Loopback, new IPAddress(header.AsSpan(20, 4)));
            Assert.Equal(clientPort, BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(24, 2)));
            Assert.Equal(listenPort, BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(26, 2)));
        }
    }

    [SkippableFact]
    public async Task V2_for_IPv6()
    {
        Skip.IfNot(NetHelpers.IPv6Available, "This machine has no IPv6 loopback.");
        var (accepted, client, clientPort, listenPort) = await ConnectAsync(IPAddress.IPv6Loopback, IPAddress.IPv6Loopback);
        using (accepted) using (client)
        {
            var header = Server.CreateProxyHeader(accepted, true);

            Assert.Equal(52, header.Length);
            Assert.Equal(0x21, header[13]); // TCP over IPv6
            Assert.Equal(36, BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(14, 2)));
            Assert.Equal(IPAddress.IPv6Loopback, new IPAddress(header.AsSpan(16, 16)));
            Assert.Equal(IPAddress.IPv6Loopback, new IPAddress(header.AsSpan(32, 16)));
            Assert.Equal(clientPort, BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(48, 2)));
            Assert.Equal(listenPort, BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(50, 2)));
        }
    }
}
