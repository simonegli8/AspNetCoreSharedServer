
using AspNetCoreSharedServer.Util;
using EstrellasDeEsperanza.AsyncLock;
using Hardware.Info;
using Mono.Cecil;
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Contracts;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace AspNetCoreSharedServer;

public class Server
{
    public const int BufferSize = 4 * 1024;
    public const string ShutdownMessage = "Application is shutting down...";
    public ILogger Logger => Configuration.Current.Logger;
    public CancellationTokenSource TcpCancel = new CancellationTokenSource();
    public CancellationTokenSource UdpCancel = new CancellationTokenSource();

    static AsyncLock UidLock = new AsyncLock();
    public async Task<TcpClient> Connect(int port, AddressFamily family)
    {
        if (port > -1)
        {
            var client = new TcpClient(family);
            var cancel = new CancellationTokenSource();
            cancel.CancelAfter(30000);

            var loopback = family == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Loopback : IPAddress.Loopback;
            try
            {
                await client.ConnectAsync(new IPEndPoint(loopback, port), cancel.Token);
            }
            catch (Exception ex)
            {
            }
            while (!client.Connected)
            {
                await Task.Delay(10, cancel.Token);
                try
                {
                    await client.ConnectAsync(new IPEndPoint(loopback, port), cancel.Token);
                }
                catch (Exception ex)
                {
                }
            }
            return client;
        }
        return null;
    }
    public async Task<TcpClient> HttpDest(AddressFamily family)
    {
        return await Connect(HttpPort, family);
    }
    public async Task<TcpClient> HttpsDest(AddressFamily family)
    {
        return await Connect(HttpsPort, family);
    }
    public async Task<TcpClient> NetTcpDest(AddressFamily family)
    {
        return await Connect(NetTcpPort, family);
    }
    public int HttpPort = -1, HttpsPort = -1, NetTcpPort = -1;
    public UdpClient? QuicHttpDestV4, QuicHttpsDestV4, QuicHttpDestV6, QuicHttpsDestV6;
    public DateTime Started = DateTime.Now;
    public long Ticks = DateTime.Now.ToBinary();
    int KestrelRestarts = 0;
    const int MaxKestrelRestarts = 9; // Maximum number of times to restart Kestrel before giving up
    public DateTime LastWork => DateTime.FromBinary(Interlocked.Read(ref Ticks));
    Proxy Proxy;
    public Application Application => Proxy.Application;
    public bool IsDotnet => Application.Assembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
    public const string LoopbackTextV4 = "127.0.0.1";
    public const string LoopbackTextV6 = "[::1]";
    Process ServerProcess = new Process();
    //public CancellationTokenSource Cancel = new CancellationTokenSource();

    public virtual string Find(string cmd) => Services.Shell.Standard.Find(cmd);

    public Server(Proxy proxy)
    {
        Proxy = proxy;
    }

    public async Task StartAsync()
    {
        if (Proxy.HasHttp) HttpPort = await Configuration.FindFreePortAsync(true);
        if (Proxy.HasHttps) HttpsPort = await Configuration.FindFreePortAsync(true);
        if (Proxy.HasNetTcp) NetTcpPort = await Configuration.FindFreePortAsync(true);

        if (Proxy.EnableHttp3)
        {
            if (HttpPort > -1)
            {
                if (Proxy.HasIPv4) QuicHttpDestV4 = new UdpClient(new IPEndPoint(IPAddress.Loopback, HttpPort));
                if (Proxy.HasIPv6) QuicHttpDestV6 = new UdpClient(new IPEndPoint(IPAddress.IPv6Loopback, HttpPort));
            }
            if (HttpsPort > -1)
            {
                if (Proxy.HasIPv4) QuicHttpsDestV4 = new UdpClient(new IPEndPoint(IPAddress.Loopback, HttpsPort));
                if (Proxy.HasIPv6) QuicHttpsDestV6 = new UdpClient(new IPEndPoint(IPAddress.IPv6Loopback, HttpsPort));
            }
        }

        var urls = new StringBuilder();
        if (Proxy.HasHttp) urls.Append($"http://{LoopbackTextV4}:{HttpPort};http://{LoopbackTextV6}:{HttpPort}");
        if (Proxy.HasHttps)
        {
            if (urls.Length > 0) urls.Append(';');
            urls.Append($"https://{LoopbackTextV4}:{HttpsPort};https://{LoopbackTextV6}:{HttpsPort}");
        }
        if (Proxy.HasNetTcp)
        {
            if (urls.Length > 0) urls.Append(';');
            urls.Append($"net.tcp://{LoopbackTextV4}:{NetTcpPort};net.tcp://{LoopbackTextV6}:{NetTcpPort}");
        }
        var info = new ProcessStartInfo();
        var user = Application.User ?? Configuration.Current.User;
        var group = Application.Group ?? Configuration.Current.Group;

        if (!File.Exists(Application.Assembly))
        {
            Logger.LogError($"Assembly {Application.Assembly} not found.");
            await Application.SetStatusAsync(Status.Error, $"Assembly not found.", true);
            return;
        }

        info.WorkingDirectory = !string.IsNullOrEmpty(Application.WorkingDirectory) ?
            Application.WorkingDirectory :
            Application.Assembly.Contains(Path.DirectorySeparatorChar) ?
                Path.GetDirectoryName(Application.Assembly) :
                    (OSInfo.IsWindows ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) :
                        (OSInfo.IsMac ? $"/Users/{user}" :
                            (user == "root" ? "/root" : $"/home/{user}")));
        bool dotnet = IsDotnet;
        var arguments = Application.Arguments
            ?.Replace("${httpport}", HttpPort.ToString())
            ?.Replace("${httpsport}", HttpsPort.ToString())
            ?.Replace("${nettcpport}", NetTcpPort.ToString())
            ?.Replace("${loopbackV4}", LoopbackTextV4)
            ?.Replace("${loopbackV6}", LoopbackTextV6);
        uint? uid = null, gid = null;

        if (!OSInfo.IsWindows && Unix.IsRoot &&
            !string.IsNullOrEmpty(user))
        {
            if (!OSInfo.IsMac)
            {
                string groupArg = "";
                if (!string.IsNullOrEmpty(group)) groupArg = $"-g {group} ";
                // If running as root, use sudo to drop privileges
                var env = $"ASPNETCORE_URLS={urls} ORIGINAL_URLS={Application.Urls ?? ""} ";
                info.FileName = "sudo";
                var dotnetwithpath = Find("dotnet") ?? "dotnet";
                info.Arguments = $"-u {user} {groupArg}{env}-- {(dotnet ? $"\"{dotnetwithpath}\" " : "")}\"{Application.Assembly}\"{(string.IsNullOrEmpty(arguments) ? "" : " " + arguments)}";
            }
            else
            {
                var userrec = Unix.getpwnam(user);
                var grouprec = !string.IsNullOrEmpty(group) ? Unix.getgrnam(group) : IntPtr.Zero;
                if (userrec == null)
                {
                    Logger.LogError($"User {user} not found.");
                    await Application.SetStatusAsync(Status.Error, $"User {user} not found.", true);
                    return;
                }
                else
                {
                    var rec = Marshal.PtrToStructure<Unix.Passwd>(userrec);
                    uid = rec.pw_uid;
                    gid = rec.pw_gid;
                }
                if (!string.IsNullOrEmpty(group) && grouprec == null)
                {
                    Logger.LogError($"Group {group} not found.");
                    await Application.SetStatusAsync(Status.Error, $"Group {group} not found.", true);
                    return;
                }
                else if (grouprec != null)
                {
                    var rec = Marshal.PtrToStructure<Unix.Group>(grouprec);
                    gid = rec.gr_gid;
                }

                if (dotnet)
                {
                    info.FileName = Find("dotnet") ?? "dotnet";
                    info.Arguments = $"\"{Application.Assembly}\"{(!string.IsNullOrEmpty(arguments) ? "" : " " + arguments)}";
                }
                else
                {  // Assembly is AOT
                    info.FileName = Application.Assembly;
                    info.Arguments = arguments ?? "";
                }
            }
        }
        else
        {
            // Otherwise, run dotnet directly
            if (dotnet)
            {
                info.FileName = Find("dotnet") ?? "dotnet";
                info.Arguments = $"\"{Application.Assembly}\"{(!string.IsNullOrEmpty(arguments) ? "" : " " + arguments)}";
            }
            else
            {  // Assembly is AOT
                info.FileName = Application.Assembly;
                info.Arguments = arguments ?? "";
            }
        }

        info.CreateNoWindow = false;
        foreach (var key in Application.Environment?.Keys ?? Enumerable.Empty<string>())
        {
            info.Environment[key] = Application.Environment![key];
        }
        info.Environment["ORIGINAL_URLS"] = Application.Urls ?? "";
        info.Environment["ASPNETCORE_URLS"] = urls.ToString();
        Logger.LogInformation($"Starting {Application.Name} on {urls}");
        info.RedirectStandardError = info.RedirectStandardOutput = true;
        info.RedirectStandardInput = true;
        info.UseShellExecute = false;
        info.WindowStyle = ProcessWindowStyle.Normal;
        Logger.LogInformation($"{(!string.IsNullOrEmpty(user) ? user : "")}>{info.FileName} {info.Arguments}");
        ServerProcess = new Process();
        ServerProcess.StartInfo = info;
        ServerProcess.EnableRaisingEvents = true;
        ServerProcess.Exited += async (sender, args) => await OnExitAsync();
        ServerProcess.OutputDataReceived += async (_, args) => await OnOutputAsync(args.Data, Console.Out);
        ServerProcess.ErrorDataReceived += async (_, args) => await OnOutputAsync(args.Data, Console.Error);

        if (gid != null || uid != null)
        {
            using (await UidLock.LockAsync())
            {
                // Switch group first, then user
                if (gid != null && Unix.setegid(gid.Value) != 0 || uid != null && Unix.seteuid(uid.Value) != 0)
                {
                    Logger.LogError("Failed to switch user/group (need to run as root).");
                    await Application.SetStatusAsync(Status.Error, $"Failed to switch user/group (need to run as root).", true);
                    return;
                }

                ServerProcess.Start();
                // Revert to root
                if ((gid != null || uid != null) && (Unix.setegid(0) != 0 || Unix.seteuid(0) != 0))
                {
                    Logger.LogError("Failed to switch back to root.");
                    await Application.SetStatusAsync(Status.Error, $"Failed to switch back to root.", true);
                    return;
                }
            }
        }
        else
        {
            ServerProcess.Start();
        }

        ServerProcess.BeginErrorReadLine();
        ServerProcess.BeginOutputReadLine();

        Ticks = DateTime.Now.ToBinary();

        if (Proxy.Recycle != null || Proxy.IdleTimeout != null ||
        Proxy.Recycle != TimeSpan.Zero || Proxy.IdleTimeout != TimeSpan.Zero)
            _ = Task.Run(async () => await CheckTimeoutAsync());

        await CheckStartedAsync();
    }

    private async Task OnExitAsync()
    {
        bool fail = false;

        await Proxy.DetachAsync();
        
        TcpCancel.Cancel();
        if (!shuttingDown)
        {
            fail = await Proxy.FailAsync();
        }
    }

    public async Task OnOutputAsync(string? data, TextWriter writer)
    {
        if (data != null)
        {
            if (data.Contains(ShutdownMessage))
            {
                await Proxy.DetachAsync();
            }
            writer.WriteLine(data);
        }
    }

    public async Task CheckStartedAsync()
    {
        if (ServerProcess?.HasExited != false)
        {
            Proxy.Server = null;
            await Proxy.FailAsync();
            Logger.LogError($"{Application.Name}: Failed to start application.");
            await Application.SetStatusAsync(Status.Error, "Failed to start application.", true);
        }
    }

    bool shuttingDown = false;
    public async Task ShutdownAsync(bool setStatus = true, bool wait = true)
    {
        //Logger.LogInformation($"Shutting down {Application.Name}.");
        if (ServerProcess != null)
        {
            shuttingDown = true;

            using (await Proxy.Lock.LockAsync())
            {
                Proxy.Server = null; // Clear the server reference to prevent further processing
                UdpCancel.Cancel();
            }

            if (!ServerProcess.HasExited)
            {
                if (setStatus) await Application.SetStatusAsync(Status.Stopping, null);
                SignalSender.SendSigint(ServerProcess); // Send SIGINT to gracefully stop Kestrel
            }

            if (wait)
            {
                var timeout = TimeSpan.FromMinutes(4);
                var start = DateTime.Now;
                do
                {
                    await Task.Delay(100);
                } while (!ServerProcess.HasExited || DateTime.Now - start < timeout);

                if (!ServerProcess.HasExited) ServerProcess.Kill();

                TcpCancel.Cancel();
            }
        }
    }

    public int ProcessId => ServerProcess?.Id ?? 0;

    public async Task CheckTimeoutAsync()
    {
        do
        {
            var now = DateTime.Now;
            var hardwareInfo = new HardwareInfo();
            hardwareInfo.RefreshMemoryStatus();

            ulong total = hardwareInfo.MemoryStatus.TotalPhysical;
            ulong available = hardwareInfo.MemoryStatus.AvailablePhysical;
            var usedmem = (double)(total-available) / (double)total;

            var lowmem = usedmem < Configuration.Current.MemoryLowThreshold;
            var idleTimeout = lowmem ?
                Proxy.IdleTimeout :
                Configuration.Current.IdleTimeoutOnLowMemory ?? Configuration.DefaultIdleTimeoutOnLowMemory;

            if ((Proxy.IdleTimeout != null && Proxy.IdleTimeout != TimeSpan.Zero || lowmem) &&
                now - LastWork > idleTimeout ||
                Proxy.Recycle != null && Proxy.Recycle != TimeSpan.Zero &&
                now - Started > Proxy.Recycle)
            {
                Logger.LogInformation($"Shutdown {Application.Name}.");
                await ShutdownAsync(false, false);
                if (Proxy.IdleTimeout == TimeSpan.Zero)
                {
                    // Immediately restart when IdleTimeout is set to zero
                    await Proxy.StartServerAsync();
                }
                return;
            }
            else
            {
                /*if (ServerProcess.HasExited)
				{
					using (await Proxy.Lock.LockAsync()) Proxy.Server = null;
					if (!shuttingDown && !await Proxy.FailAsync())
					{
						//await Proxy.StartServerAsync();
					}
				}*/
            }
            await Task.Delay(1000);
        } while (!TcpCancel.IsCancellationRequested && !UdpCancel.IsCancellationRequested);

        /*if (Proxy.Cancel.IsCancellationRequested)
		{
            Logger.LogInformation($"Shutdown {Application.Name}.");
            Shutdown();
		}*/
    }

    private static readonly byte[] ProxyV2Signature =
    {
        0x0D, 0x0A, 0x0D, 0x0A,
        0x00, 0x0D, 0x0A, 0x51,
        0x55, 0x49, 0x54, 0x0A
    };

    public static byte[] CreateProxyV2Header(
        IPAddress sourceAddress,
        IPAddress destinationAddress,
        ushort sourcePort,
        ushort destinationPort,
        ProtocolType protocol)
    {
        bool ipv4 = sourceAddress.AddressFamily == AddressFamily.InterNetwork;
        bool ipv6 = sourceAddress.AddressFamily == AddressFamily.InterNetworkV6;

        if (sourceAddress.AddressFamily != destinationAddress.AddressFamily)
            throw new ArgumentException(
                "Source and destination address families must match.");

        byte familyProtocol = protocol switch
        {
            ProtocolType.Tcp when ipv4 => 0x11, // TCP over IPv4
            ProtocolType.Udp when ipv4 => 0x12, // UDP over IPv4
            ProtocolType.Tcp when ipv6 => 0x21, // TCP over IPv6
            ProtocolType.Udp when ipv6 => 0x22, // UDP over IPv6
            _ => throw new ArgumentException(
                "Only TCP and UDP over IPv4/IPv6 are supported.")
        };

        int addressLength = ipv4 ? 12 : 36;

        byte[] buffer = new byte[16 + addressLength];

        int offset = 0;

        // Signature
        ProxyV2Signature.CopyTo(buffer, offset);
        offset += 12;

        // Version 2 + PROXY command
        buffer[offset++] = 0x21;

        // Family + protocol
        buffer[offset++] = familyProtocol;

        // Address block length
        BinaryPrimitives.WriteUInt16BigEndian(
            buffer.AsSpan(offset, 2),
            (ushort)addressLength);
        offset += 2;

        // Source address
        sourceAddress.GetAddressBytes().CopyTo(buffer, offset);
        offset += ipv4 ? 4 : 16;

        // Destination address
        destinationAddress.GetAddressBytes().CopyTo(buffer, offset);
        offset += ipv4 ? 4 : 16;

        // Source port
        BinaryPrimitives.WriteUInt16BigEndian(
            buffer.AsSpan(offset, 2),
            sourcePort);
        offset += 2;

        // Destination port
        BinaryPrimitives.WriteUInt16BigEndian(
            buffer.AsSpan(offset, 2),
            destinationPort);

        return buffer;
    }
    public async Task CopyAsync(TcpClient source, TcpClient destination, CancellationTokenSource cancel)
    {
        if (source == null || destination == null) return;

        var cts = new CancellationTokenSource();
        using (var srcStream = source.GetStream())
        using (var destStream = destination.GetStream())
        using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token, cts.Token))
        {
            // Send proxy header
            if (Application.EnableProxyHeader == true || Application.EnableProxyV2Header == true)
            {
                var externalEp = (IPEndPoint?)source.Client.RemoteEndPoint;
                var localEp = (IPEndPoint?)destination.Client.LocalEndPoint;

                byte[] proxyHeader;
                if (externalEp != null && localEp != null)
                {
                    if (externalEp.Address.AddressFamily != localEp.Address.AddressFamily)
                        throw new NotSupportedException("Proxy does not support IPv4 and IPv6 mix.");

                    if (Application.EnableProxyV2Header == true)
                    {
                        // Construct PROXY v2 header
                        proxyHeader = CreateProxyV2Header(externalEp.Address, localEp.Address, (ushort)externalEp.Port, (ushort)localEp.Port, ProtocolType.Tcp);
                    }
                    else
                    {
                        // Construct standard PROXY v1 line: PROXY TCP4/6 [Source_IP] [Dest_IP] [Source_Port] [Dest_Port]\r\n
                        var version = externalEp.Address.AddressFamily == AddressFamily.InterNetworkV6 ? "TCP6" : "TCP4"; 
                        string proxyHeaderText = $"PROXY {version} {externalEp.Address} {localEp.Address} {externalEp.Port} {localEp.Port}\r\n";
                        proxyHeader = Encoding.ASCII.GetBytes(proxyHeaderText);
                    }
                    // Inject the PROXY header
                    await destStream.WriteAsync(proxyHeader, 0, proxyHeader.Length, linkedCts.Token);

                    if (Application.EnableProxyV2Header == true)
                    {
                        Logger.LogInformation("Sent PROXY v2 header");
                    } else
                    {
                        Logger.LogInformation("Sent PROXY v1 header");
                    }
                }
            }
            
            // Start bi‑directional copy tasks
            var t1 = Pump(srcStream, destStream, linkedCts.Token); // client → server
            var t2 = Pump(destStream, srcStream, linkedCts.Token); // server →  client

            try
            {
                await Task.WhenAny(t1, t2);
                cts.Cancel();
                await Task.WhenAll(t1, t2);
            } catch (Exception ex)
            {
                Logger?.LogWarning($"Exception when copying data {ex.Message}");
            }
        }
    }

    static async Task Pump(NetworkStream src, NetworkStream dst, CancellationToken ct)
    {
        try
        {
            await src.CopyToAsync(dst, ct);
        }
        catch (OperationCanceledException) { /* expected on shutdown */ }
        catch (IOException) { /* connection reset */ }
    
        /*var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int read = await src.ReadAsync(buffer, 0, buffer.Length, ct);
                if (read == 0)
                    break; // remote closed
                await dst.WriteAsync(buffer, 0, read, ct);
                await dst.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException) {  }
        catch (IOException) { }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }*/
    }
}