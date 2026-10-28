using System.Net;
using System.Net.Sockets;
using Xunit;

namespace AspNetCoreSharedServer.Tests;

/// <summary>Port selection for the app (Kestrel uses one number for TCP and for QUIC), and when a proxy forwards HTTP/3.</summary>
public class PortAndProxyTests
{
    [Fact]
    public async Task A_port_picked_for_HTTP3_is_free_for_TCP_and_for_UDP()
    {
        for (int i = 0; i < 5; i++)
        {
            var port = await Configuration.FindFreePortAsync(temporary: true, alsoUdp: true);

            Assert.InRange(port, 1, 65535);
            using var tcp = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            tcp.Bind(new IPEndPoint(IPAddress.Any, port));
            using var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            udp.Bind(new IPEndPoint(IPAddress.Any, port));
            if (NetHelpers.IPv6Available)
            {
                using var udp6 = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);
                udp6.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
            }
        }
    }

    static Proxy NewProxy(bool? enableHttp3 = null, bool? proxyHeader = null, bool? proxyV2Header = null) =>
        new(new Application
        {
            Name = "Test",
            Assembly = "test.dll",
            EnableHttp3 = enableHttp3,
            EnableProxyHeader = proxyHeader,
            EnableProxyV2Header = proxyV2Header,
        });

    [Fact]
    public void HTTP3_is_on_by_default()
    {
        Assert.True(NewProxy().EnableHttp3);
    }

    [Fact]
    public void HTTP3_can_be_switched_off_for_an_application()
    {
        Assert.False(NewProxy(enableHttp3: false).EnableHttp3);
    }

    [Theory]
    [InlineData(true, null)]
    [InlineData(null, true)]
    [InlineData(true, true)]
    public void HTTP3_is_off_when_a_PROXY_header_is_enabled_because_UDP_cannot_carry_it(bool? v1, bool? v2)
    {
        Assert.False(NewProxy(proxyHeader: v1, proxyV2Header: v2).EnableHttp3);
    }

    [Fact]
    public void A_disabled_PROXY_header_leaves_HTTP3_on()
    {
        Assert.True(NewProxy(proxyHeader: false, proxyV2Header: false).EnableHttp3);
    }
}
