using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace AspNetCoreSharedServer;

/// <summary>
/// Describes where the relay sends datagrams for one running app. It is compared by reference: when the app is
/// restarted it gets a new backend, and the sessions of the old one are replaced.
/// </summary>
public sealed class UdpBackend
{
    readonly Action touch;

    public UdpBackend(int port, CancellationToken cancel, Action touch)
    {
        Port = port;
        Cancel = cancel;
        this.touch = touch;
    }

    /// <summary>The loopback port the app listens on (for QUIC the same number as its TCP port).</summary>
    public int Port { get; }
    /// <summary>Cancelled when the app goes away; all sessions towards it end.</summary>
    public CancellationToken Cancel { get; }
    /// <summary>Records that traffic flowed, so that the app does not count as idle.</summary>
    public void Touch() => touch();
}

/// <summary>
/// Relays UDP datagrams, in practice QUIC for HTTP/3, between clients and an app, like a NAT: every client endpoint gets its
/// own session with a dedicated socket towards the app, so the app sees separate peers and replies can be routed back to the
/// right client. The relay does not interpret the datagrams.
/// </summary>
public sealed class UdpRelay : IDisposable
{
    /// <summary>A session that has seen no datagram in either direction for this long is closed.</summary>
    public static TimeSpan SessionIdleTimeout { get; set; } = TimeSpan.FromSeconds(60);
    /// <summary>Upper bound for concurrent sessions, so that spoofed source addresses cannot exhaust sockets.</summary>
    public static int MaxSessions { get; set; } = 4096;
    /// <summary>
    /// A new session is only created for a datagram of at least this size. A QUIC client's first packet must be padded to
    /// 1200 bytes, so this ignores the small packets that a flood of spoofed addresses would use.
    /// </summary>
    public const int MinNewSessionSize = 1200;

    const int MaxDatagramSize = 65535;
    /// <summary>
    /// Receive buffer of every session for the app's replies. QUIC datagrams are limited by the network MTU (about 1500 bytes),
    /// so this is generous, and with up to MaxSessions sessions it keeps the memory bounded (the shared 64 KB would not).
    /// </summary>
    const int SessionBufferSize = 16 * 1024;
    const int ReceiveBufferBytes = 1 << 20;

    readonly List<Socket> sockets = new();
    readonly ConcurrentDictionary<SessionKey, Session> sessions = new();
    readonly Func<UdpBackend?> getBackend;
    readonly ILogger? logger;
    long lastCapWarning = long.MinValue;
    int disposed;

    readonly record struct SessionKey(int Socket, IPEndPoint Client);

    /// <summary>Binds one UDP socket per endpoint. Throws a SocketException when one cannot be bound.</summary>
    /// <param name="endpoints">The public addresses and port to listen on.</param>
    /// <param name="getBackend">Returns the running app, or null if there is none. The relay never starts an app: without a
    /// running app datagrams are dropped, clients fall back to TCP, and that starts it.</param>
    public UdpRelay(IEnumerable<IPEndPoint> endpoints, Func<UdpBackend?> getBackend, ILogger? logger = null)
    {
        this.getBackend = getBackend;
        this.logger = logger;
        try
        {
            foreach (var endpoint in endpoints)
            {
                var socket = new Socket(endpoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
                sockets.Add(socket);
                // Like the TCP listeners, every address family has its own socket.
                if (endpoint.AddressFamily == AddressFamily.InterNetworkV6) socket.DualMode = false;
                try { socket.ReceiveBufferSize = ReceiveBufferBytes; } catch (SocketException) { }
                if (OperatingSystem.IsWindows()) DisableConnectionReset(socket);
                socket.Bind(endpoint);
            }
        }
        catch
        {
            foreach (var socket in sockets) socket.Dispose();
            throw;
        }
        LocalEndPoints = sockets.Select(s => (IPEndPoint)s.LocalEndPoint!).ToArray();
    }

    /// <summary>The addresses the relay actually listens on (with the real port if 0 was requested).</summary>
    public IReadOnlyList<IPEndPoint> LocalEndPoints { get; }
    public int SessionCount => sessions.Count;

    /// <summary>Windows reports an ICMP "port unreachable" for any peer as an error on a receive of an unconnected socket.</summary>
    static void DisableConnectionReset(Socket socket)
    {
        const int SIO_UDP_CONNRESET = -1744830452;
        try { socket.IOControl(SIO_UDP_CONNRESET, new byte[] { 0 }, null); } catch (SocketException) { }
    }

    /// <summary>Relays until cancelled, then releases all sockets.</summary>
    public async Task RunAsync(CancellationToken cancel)
    {
        try
        {
            var loops = sockets.Select((socket, index) => ReceiveLoopAsync(socket, index, cancel)).ToList();
            loops.Add(SweepLoopAsync(cancel));
            await Task.WhenAll(loops);
        }
        finally
        {
            Dispose();
        }
    }

    async Task ReceiveLoopAsync(Socket socket, int index, CancellationToken cancel)
    {
        var buffer = new byte[MaxDatagramSize];
        EndPoint any = socket.AddressFamily == AddressFamily.InterNetworkV6 ? new IPEndPoint(IPAddress.IPv6Any, 0) : new IPEndPoint(IPAddress.Any, 0);
        while (!cancel.IsCancellationRequested)
        {
            SocketReceiveFromResult received;
            try
            {
                received = await socket.ReceiveFromAsync(buffer, SocketFlags.None, any, cancel);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.OperationAborted) { break; }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset) { continue; }
            catch (SocketException ex)
            {
                logger?.LogWarning(ex, "UDP receive failed: {Message}", ex.Message);
                try { await Task.Delay(100, cancel); } catch (OperationCanceledException) { break; }
                continue;
            }

            try
            {
                await HandleAsync(socket, index, (IPEndPoint)received.RemoteEndPoint, buffer.AsMemory(0, received.ReceivedBytes), cancel);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger?.LogWarning(ex, "UDP datagram from {Client} was dropped: {Message}", received.RemoteEndPoint, ex.Message);
            }
        }
    }

    async ValueTask HandleAsync(Socket publicSocket, int index, IPEndPoint client, ReadOnlyMemory<byte> datagram, CancellationToken cancel)
    {
        var backend = getBackend();
        if (backend == null || backend.Cancel.IsCancellationRequested) return; // no running app: never start one for UDP

        var key = new SessionKey(index, client);
        if (!sessions.TryGetValue(key, out var session) || session.IsDisposed || session.Backend != backend)
        {
            if (datagram.Length < MinNewSessionSize) return;
            if (sessions.Count >= MaxSessions)
            {
                var now = Environment.TickCount64;
                if (now - lastCapWarning > 60_000)
                {
                    lastCapWarning = now;
                    logger?.LogWarning("UDP relay is at its limit of {Max} sessions, dropping datagrams from new clients.", MaxSessions);
                }
                return;
            }
            session?.Dispose(); // it belonged to an earlier run of the app (or has ended)
            session = new Session(this, key, publicSocket, client, backend, cancel);
            sessions[key] = session;
        }

        session.Touch();
        backend.Touch();
        await session.SendAsync(datagram);
    }

    async Task SweepLoopAsync(CancellationToken cancel)
    {
        var timeoutMs = (long)SessionIdleTimeout.TotalMilliseconds;
        var interval = TimeSpan.FromMilliseconds(Math.Clamp(timeoutMs / 2, 50, 10_000));
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancel))
            {
                foreach (var session in sessions.Values)
                {
                    if (session.IsDisposed || session.Backend.Cancel.IsCancellationRequested ||
                        Environment.TickCount64 - session.LastActivity > timeoutMs)
                    {
                        session.Dispose(); // its reply loop removes it from the dictionary
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        foreach (var socket in sockets) socket.Dispose();
        foreach (var session in sessions.Values) session.Dispose();
    }

    /// <summary>One client endpoint and its dedicated socket towards the app.</summary>
    sealed class Session : IDisposable
    {
        readonly UdpRelay relay;
        readonly SessionKey key;
        readonly Socket publicSocket;
        readonly IPEndPoint client;
        readonly CancellationTokenSource cts;
        readonly CancellationToken token;
        Socket socket;
        AddressFamily family;
        bool replied;
        long lastActivity = Environment.TickCount64;
        int disposed;

        public Session(UdpRelay relay, SessionKey key, Socket publicSocket, IPEndPoint client, UdpBackend backend, CancellationToken relayCancel)
        {
            this.relay = relay;
            this.key = key;
            this.publicSocket = publicSocket;
            this.client = client;
            Backend = backend;

            // Talk to the app over the client's address family, like the TCP connection does, and fall back to the other one.
            var preferred = client.Address.IsIPv4MappedToIPv6 ? AddressFamily.InterNetwork : client.AddressFamily;
            try
            {
                family = preferred;
                socket = Connect(family);
            }
            catch (SocketException)
            {
                family = Other(preferred);
                socket = Connect(family);
            }

            cts = CancellationTokenSource.CreateLinkedTokenSource(relayCancel, backend.Cancel);
            token = cts.Token;
            _ = Task.Run(ReplyLoopAsync);
        }

        public UdpBackend Backend { get; }
        public bool IsDisposed => Volatile.Read(ref disposed) != 0;
        public long LastActivity => Volatile.Read(ref lastActivity);
        public void Touch() => Volatile.Write(ref lastActivity, Environment.TickCount64);

        static AddressFamily Other(AddressFamily family) => family == AddressFamily.InterNetwork ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork;

        Socket Connect(AddressFamily family)
        {
            var created = new Socket(family, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                created.Connect(new IPEndPoint(family == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Loopback : IPAddress.Loopback, Backend.Port));
                return created;
            }
            catch
            {
                created.Dispose();
                throw;
            }
        }

        public async ValueTask SendAsync(ReadOnlyMemory<byte> datagram)
        {
            var current = Volatile.Read(ref socket);
            try
            {
                await current.SendAsync(datagram, SocketFlags.None, token);
            }
            catch (SocketException) { }           // refused: the reply loop sees the ICMP error and switches family
            catch (ObjectDisposedException) { }   // the socket was replaced or the session ended: QUIC retransmits
            catch (OperationCanceledException) { }
        }

        async Task ReplyLoopAsync()
        {
            var buffer = new byte[SessionBufferSize];
            try
            {
                while (!token.IsCancellationRequested)
                {
                    int received;
                    try
                    {
                        received = await Volatile.Read(ref socket).ReceiveAsync(buffer, SocketFlags.None, token);
                    }
                    catch (SocketException ex) when (ex.SocketErrorCode == SocketError.MessageSize)
                    {
                        continue; // a datagram larger than the buffer is lost (QUIC retransmits), the session goes on
                    }
                    catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionRefused)
                    {
                        // The app refused a datagram (ICMP port unreachable). Until it has answered once, it may only be
                        // listening on the other loopback family, so try that one.
                        if (!replied) SwitchFamily();
                        continue;
                    }

                    replied = true;
                    Touch();
                    Backend.Touch();
                    try
                    {
                        await publicSocket.SendToAsync(buffer.AsMemory(0, received), SocketFlags.None, client, token);
                    }
                    catch (SocketException) { } // the client is gone
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (SocketException ex)
            {
                relay.logger?.LogWarning(ex, "UDP session for {Client} ended: {Message}", client, ex.Message);
            }
            finally
            {
                Dispose();
                relay.sessions.TryRemove(new KeyValuePair<SessionKey, Session>(key, this));
                cts.Dispose();
            }
        }

        void SwitchFamily()
        {
            var next = Other(family);
            Socket fresh;
            try { fresh = Connect(next); }
            catch (SocketException) { return; } // the other family is not available either: stay where we are
            var old = socket;
            family = next;
            Volatile.Write(ref socket, fresh);
            old.Dispose();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
            socket.Dispose();
        }
    }
}
