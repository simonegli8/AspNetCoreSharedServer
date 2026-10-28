using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AspNetCoreSharedServer.Tests;

/// <summary>A UDP "app": answers every datagram with "&lt;name&gt;:" followed by the datagram, and remembers the ports it saw.</summary>
internal sealed class UdpEchoApp : IDisposable
{
    readonly Socket socket;
    readonly CancellationTokenSource cts = new();

    public int Port { get; }
    /// <summary>The source ports of all datagrams received. For a relay these are the ports of its session sockets.</summary>
    public ConcurrentBag<int> SourcePorts { get; } = new();

    public UdpEchoApp(string name, AddressFamily family = AddressFamily.InterNetwork)
    {
        socket = new Socket(family, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(family == AddressFamily.InterNetwork ? IPAddress.Loopback : IPAddress.IPv6Loopback, 0));
        Port = ((IPEndPoint)socket.LocalEndPoint!).Port;
        _ = Task.Run(async () =>
        {
            var buffer = new byte[65535];
            EndPoint any = family == AddressFamily.InterNetwork ? new IPEndPoint(IPAddress.Any, 0) : new IPEndPoint(IPAddress.IPv6Any, 0);
            while (!cts.IsCancellationRequested)
            {
                SocketReceiveFromResult received;
                try { received = await socket.ReceiveFromAsync(buffer, SocketFlags.None, any, cts.Token); }
                catch { break; }
                SourcePorts.Add(((IPEndPoint)received.RemoteEndPoint).Port);
                var reply = Encoding.ASCII.GetBytes(name + ":").Concat(buffer.Take(received.ReceivedBytes)).ToArray();
                try { await socket.SendToAsync(reply, SocketFlags.None, received.RemoteEndPoint); } catch (SocketException) { }
            }
        });
    }

    public void Dispose()
    {
        cts.Cancel();
        socket.Dispose();
    }
}

internal static class NetHelpers
{
    /// <summary>False on machines without IPv6 loopback, where the IPv6 tests are skipped.</summary>
    public static bool IPv6Available { get; } = CheckIPv6();

    static bool CheckIPv6()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);
            socket.Bind(new IPEndPoint(IPAddress.IPv6Loopback, 0));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>Receives one datagram, or null if none arrives within the time.</summary>
    public static async Task<byte[]?> ReceiveAsync(UdpClient client, int milliseconds = 3000)
    {
        using var cts = new CancellationTokenSource(milliseconds);
        try { return (await client.ReceiveAsync(cts.Token)).Buffer; }
        catch (OperationCanceledException) { return null; }
    }

    public static byte[] Payload(int size, byte seed) => Enumerable.Range(0, size).Select(i => (byte)(seed + i)).ToArray();

    public static string Text(byte[]? data, int length = 8) => data == null ? "(none)" : Encoding.ASCII.GetString(data, 0, Math.Min(length, data.Length));

    /// <summary>Two connected loopback TCP connections.</summary>
    public static async Task<(TcpClient First, TcpClient Second)> TcpPairAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var first = new TcpClient();
            await first.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            var second = await listener.AcceptTcpClientAsync();
            return (first, second);
        }
        finally
        {
            listener.Stop();
        }
    }

    public static async Task<byte[]> ReadToEndAsync(NetworkStream stream)
    {
        var memory = new MemoryStream();
        try { await stream.CopyToAsync(memory); } catch (IOException) { }
        return memory.ToArray();
    }

    public static async Task<bool> CompletesAsync(Task task, int milliseconds) => await Task.WhenAny(task, Task.Delay(milliseconds)) == task;
}
