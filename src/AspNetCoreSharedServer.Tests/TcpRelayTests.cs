using System.Net.Sockets;
using System.Text;
using Xunit;

namespace AspNetCoreSharedServer.Tests;

/// <summary>
/// Tests of <see cref="Server.RelayAsync"/>, which copies a TCP connection in both directions: half-close, reset, idle bound.
/// <see cref="Server.HalfCloseIdleTimeout"/> is static and changed by one test, so all of them live in one class.
/// </summary>
public class TcpRelayTests
{
    /// <summary>clientEnd &lt;-&gt; proxy ==relay== proxy &lt;-&gt; backendEnd. Like Server.CopyAsync, the proxy side is disposed when the relay is done.</summary>
    sealed class Setup : IDisposable
    {
        readonly CancellationTokenSource cts = new();
        public TcpClient ClientEnd { get; }
        public TcpClient BackendEnd { get; }
        public Task Relay { get; }
        public void Cancel() => cts.Cancel();

        Setup(TcpClient clientEnd, TcpClient backendEnd, TcpClient front, TcpClient back)
        {
            ClientEnd = clientEnd;
            BackendEnd = backendEnd;
            Relay = Task.Run(async () =>
            {
                using (front)
                using (back)
                {
                    await Server.RelayAsync(front.GetStream(), back.GetStream(), front.Client, back.Client, cts.Token);
                }
            });
        }

        public static async Task<Setup> CreateAsync()
        {
            var (clientEnd, front) = await NetHelpers.TcpPairAsync();
            var (back, backendEnd) = await NetHelpers.TcpPairAsync();
            return new Setup(clientEnd, backendEnd, front, back);
        }

        public void Dispose()
        {
            cts.Cancel();
            ClientEnd.Dispose();
            BackendEnd.Dispose();
            Relay.Wait(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task A_half_closed_request_still_gets_its_full_response()
    {
        var payload = NetHelpers.Payload(200_000, 7);
        using var setup = await Setup.CreateAsync();

        // The app reads the request until the client's half-close arrives, takes a while, then answers with a large response.
        var app = Task.Run(async () =>
        {
            var request = await NetHelpers.ReadToEndAsync(setup.BackendEnd.GetStream());
            await Task.Delay(300);
            await setup.BackendEnd.GetStream().WriteAsync(payload);
            setup.BackendEnd.Client.Shutdown(SocketShutdown.Send);
            return request;
        });

        var stream = setup.ClientEnd.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes("REQUEST"));
        setup.ClientEnd.Client.Shutdown(SocketShutdown.Send); // half-close: no more data from the client
        var response = await NetHelpers.ReadToEndAsync(stream);

        Assert.Equal("REQUEST", Encoding.ASCII.GetString(await app));
        Assert.True(payload.AsSpan().SequenceEqual(response), $"only {response.Length} of {payload.Length} bytes arrived");
        setup.ClientEnd.Dispose();
        Assert.True(await NetHelpers.CompletesAsync(setup.Relay, 5000));
    }

    [Fact]
    public async Task Passes_on_data_and_the_close_when_the_app_closes_first()
    {
        using var setup = await Setup.CreateAsync();
        await setup.BackendEnd.GetStream().WriteAsync(Encoding.ASCII.GetBytes("HELLO"));
        setup.BackendEnd.Client.Shutdown(SocketShutdown.Send);

        var received = await NetHelpers.ReadToEndAsync(setup.ClientEnd.GetStream());
        Assert.Equal("HELLO", Encoding.ASCII.GetString(received));

        setup.ClientEnd.Dispose();
        Assert.True(await NetHelpers.CompletesAsync(setup.Relay, 5000));
    }

    [Fact]
    public async Task A_reset_tears_down_both_directions_at_once()
    {
        using var setup = await Setup.CreateAsync();

        // TcpClient.Dispose() would shut down gracefully first, so reset at the socket level: linger 0 + close = RST.
        setup.ClientEnd.Client.LingerState = new LingerOption(true, 0);
        setup.ClientEnd.Client.Dispose();

        Assert.True(await NetHelpers.CompletesAsync(setup.Relay, 5000));
        // The backend connection was closed as well.
        Assert.True(await NetHelpers.CompletesAsync(NetHelpers.ReadToEndAsync(setup.BackendEnd.GetStream()), 5000));
    }

    [Fact]
    public async Task A_silent_peer_after_a_half_close_is_cut_off_by_the_idle_bound()
    {
        var oldTimeout = Server.HalfCloseIdleTimeout;
        Server.HalfCloseIdleTimeout = TimeSpan.FromMilliseconds(600);
        try
        {
            using var setup = await Setup.CreateAsync();
            var watch = System.Diagnostics.Stopwatch.StartNew();

            setup.ClientEnd.Client.Shutdown(SocketShutdown.Send); // the app never answers and never closes

            Assert.True(await NetHelpers.CompletesAsync(setup.Relay, 5000));
            Assert.InRange(watch.ElapsedMilliseconds, 500, 3500);
        }
        finally
        {
            Server.HalfCloseIdleTimeout = oldTimeout;
        }
    }

    [Fact]
    public async Task Cancellation_ends_the_relay()
    {
        using var setup = await Setup.CreateAsync();

        setup.Cancel();

        Assert.True(await NetHelpers.CompletesAsync(setup.Relay, 5000));
    }

    [Fact]
    public async Task Relays_both_ways_while_both_sides_stay_open()
    {
        using var setup = await Setup.CreateAsync();
        var clientStream = setup.ClientEnd.GetStream();
        var backendStream = setup.BackendEnd.GetStream();
        var buffer = new byte[4];

        await clientStream.WriteAsync(Encoding.ASCII.GetBytes("ping"));
        await backendStream.ReadExactlyAsync(buffer);
        Assert.Equal("ping", Encoding.ASCII.GetString(buffer));

        await backendStream.WriteAsync(Encoding.ASCII.GetBytes("pong"));
        await clientStream.ReadExactlyAsync(buffer);
        Assert.Equal("pong", Encoding.ASCII.GetString(buffer));
    }
}
