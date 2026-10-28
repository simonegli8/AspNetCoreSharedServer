using System.Net;
using System.Net.Sockets;
using Xunit;
using Xunit.Abstractions;

namespace AspNetCoreSharedServer.Tests;

/// <summary>
/// Tests of <see cref="UdpRelay"/> with plain sockets: clients on one side, a UDP echo "app" on the other.
/// UdpRelay has static settings that some tests change, so all of them live in one class (xunit runs a class sequentially).
/// </summary>
public class UdpRelayTests
{
    /// <summary>A relay on loopback that runs until disposed.</summary>
    sealed class RunningRelay : IDisposable
    {
        readonly CancellationTokenSource cts = new();
        readonly Task run;

        public UdpRelay Relay { get; }
        public UdpBackend? Backend { get; set; }
        public IPEndPoint EndPoint => Relay.LocalEndPoints[0];
        public Task Run => run;
        public void Cancel() => cts.Cancel();

        public RunningRelay(UdpBackend? backend, IPEndPoint? bind = null)
        {
            Backend = backend;
            Relay = new UdpRelay(new[] { bind ?? new IPEndPoint(IPAddress.Loopback, 0) }, () => Backend);
            run = Relay.RunAsync(cts.Token);
        }

        public void Dispose()
        {
            cts.Cancel();
            run.Wait(TimeSpan.FromSeconds(5));
        }
    }

    static UdpBackend BackendFor(UdpEchoApp app, Action? touch = null, CancellationToken cancel = default) =>
        new(app.Port, cancel, touch ?? (() => { }));

    [Fact]
    public async Task Relays_a_datagram_and_the_reply()
    {
        using var app = new UdpEchoApp("A");
        var touches = 0;
        using var relay = new RunningRelay(BackendFor(app, () => Interlocked.Increment(ref touches)));
        using var client = new UdpClient(AddressFamily.InterNetwork);
        var data = NetHelpers.Payload(1300, 1);

        await client.SendAsync(data, relay.EndPoint);
        var reply = await NetHelpers.ReceiveAsync(client);

        Assert.NotNull(reply);
        Assert.Equal("A:", NetHelpers.Text(reply, 2));
        Assert.Equal(data, reply!.Skip(2).ToArray());
        // The app sees the relay's session socket, never the client itself.
        var clientPort = ((IPEndPoint)client.Client.LocalEndPoint!).Port;
        Assert.Single(app.SourcePorts);
        Assert.NotEqual(clientPort, app.SourcePorts.First());
        // Traffic counts as activity, so that the app does not look idle.
        Assert.True(touches > 0);
    }

    [Fact]
    public async Task Gives_every_client_its_own_session()
    {
        using var app = new UdpEchoApp("B");
        using var relay = new RunningRelay(BackendFor(app));
        using var first = new UdpClient(AddressFamily.InterNetwork);
        using var second = new UdpClient(AddressFamily.InterNetwork);

        await first.SendAsync(NetHelpers.Payload(1300, 10), relay.EndPoint);
        await second.SendAsync(NetHelpers.Payload(1300, 99), relay.EndPoint);
        var firstReply = await NetHelpers.ReceiveAsync(first);
        var secondReply = await NetHelpers.ReceiveAsync(second);

        // Each reply belongs to the client that sent the data.
        Assert.Equal(10, firstReply![2]);
        Assert.Equal(99, secondReply![2]);
        Assert.Equal(2, relay.Relay.SessionCount);
        Assert.Equal(2, app.SourcePorts.Distinct().Count());
    }

    [Fact]
    public async Task A_small_first_datagram_does_not_open_a_session()
    {
        using var app = new UdpEchoApp("C");
        using var relay = new RunningRelay(BackendFor(app));
        using var client = new UdpClient(AddressFamily.InterNetwork);

        await client.SendAsync(NetHelpers.Payload(UdpRelay.MinNewSessionSize - 1, 1), relay.EndPoint);
        Assert.Null(await NetHelpers.ReceiveAsync(client, 500));
        Assert.Equal(0, relay.Relay.SessionCount);

        // A datagram of the minimum size opens the session, after which small datagrams flow.
        await client.SendAsync(NetHelpers.Payload(UdpRelay.MinNewSessionSize, 1), relay.EndPoint);
        Assert.NotNull(await NetHelpers.ReceiveAsync(client));
        await client.SendAsync(NetHelpers.Payload(50, 5), relay.EndPoint);
        var small = await NetHelpers.ReceiveAsync(client);
        Assert.Equal(52, small?.Length);
    }

    [Fact]
    public async Task Drops_datagrams_while_no_app_is_running()
    {
        using var app = new UdpEchoApp("D");
        using var relay = new RunningRelay(null); // UDP never starts an app, so without one nothing is relayed
        using var client = new UdpClient(AddressFamily.InterNetwork);

        await client.SendAsync(NetHelpers.Payload(1300, 1), relay.EndPoint);

        Assert.Null(await NetHelpers.ReceiveAsync(client, 600));
        Assert.Equal(0, relay.Relay.SessionCount);
        Assert.Empty(app.SourcePorts);
    }

    [Fact]
    public async Task Moves_a_client_to_the_restarted_app()
    {
        using var first = new UdpEchoApp("E1");
        using var second = new UdpEchoApp("E2");
        using var relay = new RunningRelay(BackendFor(first));
        using var client = new UdpClient(AddressFamily.InterNetwork);

        await client.SendAsync(NetHelpers.Payload(1300, 1), relay.EndPoint);
        var before = await NetHelpers.ReceiveAsync(client);
        relay.Backend = BackendFor(second); // a restarted app is a new backend
        await client.SendAsync(NetHelpers.Payload(1300, 1), relay.EndPoint);
        var after = await NetHelpers.ReceiveAsync(client);

        Assert.Equal("E1:", NetHelpers.Text(before, 3));
        Assert.Equal("E2:", NetHelpers.Text(after, 3));
        Assert.Equal(1, relay.Relay.SessionCount); // the old session was replaced, not added to
    }

    [Fact]
    public async Task Expires_idle_sessions()
    {
        var oldTimeout = UdpRelay.SessionIdleTimeout;
        UdpRelay.SessionIdleTimeout = TimeSpan.FromMilliseconds(600);
        try
        {
            using var app = new UdpEchoApp("F");
            using var relay = new RunningRelay(BackendFor(app));
            using var client = new UdpClient(AddressFamily.InterNetwork);

            await client.SendAsync(NetHelpers.Payload(1300, 1), relay.EndPoint);
            await NetHelpers.ReceiveAsync(client);
            Assert.Equal(1, relay.Relay.SessionCount);

            await Task.Delay(2000);
            Assert.Equal(0, relay.Relay.SessionCount);
        }
        finally
        {
            UdpRelay.SessionIdleTimeout = oldTimeout;
        }
    }

    [Fact]
    public async Task Refuses_new_clients_at_the_session_limit()
    {
        var oldMax = UdpRelay.MaxSessions;
        UdpRelay.MaxSessions = 2;
        try
        {
            using var app = new UdpEchoApp("G");
            using var relay = new RunningRelay(BackendFor(app));
            var clients = Enumerable.Range(0, 3).Select(_ => new UdpClient(AddressFamily.InterNetwork)).ToList();
            try
            {
                var replies = new List<byte[]?>();
                foreach (var client in clients)
                {
                    await client.SendAsync(NetHelpers.Payload(1300, 1), relay.EndPoint);
                    replies.Add(await NetHelpers.ReceiveAsync(client, 800));
                }

                Assert.NotNull(replies[0]);
                Assert.NotNull(replies[1]);
                Assert.Null(replies[2]);
                Assert.Equal(2, relay.Relay.SessionCount);
            }
            finally
            {
                clients.ForEach(client => client.Dispose());
            }
        }
        finally
        {
            UdpRelay.MaxSessions = oldMax;
        }
    }

    [SkippableFact]
    public async Task Falls_back_to_IPv4_for_an_IPv6_client_when_the_app_only_listens_on_IPv4()
    {
        Skip.IfNot(NetHelpers.IPv6Available, "This machine has no IPv6 loopback.");
        using var app = new UdpEchoApp("H"); // IPv4 only
        using var relay = new RunningRelay(BackendFor(app), new IPEndPoint(IPAddress.IPv6Loopback, 0));
        using var client = new UdpClient(AddressFamily.InterNetworkV6);

        // The session first tries [::1], is refused (ICMP) and switches to 127.0.0.1. QUIC would retransmit like this.
        byte[]? reply = null;
        for (int attempt = 0; attempt < 15 && reply == null; attempt++)
        {
            await client.SendAsync(NetHelpers.Payload(1300, 1), relay.EndPoint);
            reply = await NetHelpers.ReceiveAsync(client, 300);
        }

        Assert.Equal("H:", NetHelpers.Text(reply, 2));
    }

    [SkippableFact]
    public async Task Uses_IPv6_for_an_IPv6_client_when_the_app_listens_on_IPv6()
    {
        Skip.IfNot(NetHelpers.IPv6Available, "This machine has no IPv6 loopback.");
        using var app = new UdpEchoApp("I", AddressFamily.InterNetworkV6);
        using var relay = new RunningRelay(BackendFor(app), new IPEndPoint(IPAddress.IPv6Loopback, 0));
        using var client = new UdpClient(AddressFamily.InterNetworkV6);

        await client.SendAsync(NetHelpers.Payload(1300, 1), relay.EndPoint);

        Assert.Equal("I:", NetHelpers.Text(await NetHelpers.ReceiveAsync(client), 2));
    }

    [Fact]
    public async Task Ends_sessions_when_the_app_goes_away()
    {
        var oldTimeout = UdpRelay.SessionIdleTimeout;
        UdpRelay.SessionIdleTimeout = TimeSpan.FromSeconds(1); // sweeps every 500 ms
        try
        {
            using var app = new UdpEchoApp("J");
            using var gone = new CancellationTokenSource();
            using var relay = new RunningRelay(BackendFor(app, cancel: gone.Token));
            using var client = new UdpClient(AddressFamily.InterNetwork);

            await client.SendAsync(NetHelpers.Payload(1300, 1), relay.EndPoint);
            Assert.NotNull(await NetHelpers.ReceiveAsync(client));

            gone.Cancel();
            await Task.Delay(1500);
            await client.SendAsync(NetHelpers.Payload(1300, 1), relay.EndPoint);

            Assert.Null(await NetHelpers.ReceiveAsync(client, 600));
            Assert.Equal(0, relay.Relay.SessionCount);
        }
        finally
        {
            UdpRelay.SessionIdleTimeout = oldTimeout;
        }
    }

    [Fact]
    public async Task Stops_and_releases_its_port_when_cancelled()
    {
        using var app = new UdpEchoApp("K");
        var relay = new RunningRelay(BackendFor(app));
        var port = relay.EndPoint.Port;

        relay.Cancel();
        Assert.True(await NetHelpers.CompletesAsync(relay.Run, 3000));

        // The port can be bound again.
        using var again = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
    }
}
