
using System;
using System.Buffers;
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
using AspNetCoreSharedServer.Util;
using Hardware.Info;
using Mono.Cecil;
using EstrellasDeEsperanza.AsyncLock;

namespace AspNetCoreSharedServer;

public class Server
{
    public const int BufferSize = 4 * 1024;
    public const string ShutdownMessage = "Application is shutting down...";
    public ILogger Logger => Configuration.Current.Logger;
    public CancellationTokenSource TcpCancel = new CancellationTokenSource();
    public CancellationTokenSource UdpCancel = new CancellationTokenSource();

    static AsyncLock UidLock = new AsyncLock();
    public async Task<TcpClient> Connect(int port)
    {
        if (port > -1)
        {
            var client = new TcpClient();
            var cancel = new CancellationTokenSource();
            cancel.CancelAfter(30000);

            try
            {
                await client.ConnectAsync(new IPEndPoint(Loopback, port), cancel.Token);
            }
            catch (Exception ex)
            {
            }
            while (!client.Connected)
            {
                await Task.Delay(10, cancel.Token);
                try
                {
                    await client.ConnectAsync(new IPEndPoint(Loopback, port), cancel.Token);
                }
                catch (Exception ex)
                {
                }
            }
            return client;
        }
        return null;
    }
    public async Task<TcpClient> HttpDest()
    {
        return await Connect(HttpPort);
    }
    public async Task<TcpClient> HttpsDest()
    {
        return await Connect(HttpsPort);
    }
    public async Task<TcpClient> NetTcpDest()
    {
        return await Connect(NetTcpPort);
    }
    public int HttpPort = -1, HttpsPort = -1, NetTcpPort = -1;
    public UdpClient? QuicHttpDest, QuicHttpsDest;
    public DateTime Started = DateTime.Now;
    public long Ticks = DateTime.Now.ToBinary();
    int KestrelRestarts = 0;
    const int MaxKestrelRestarts = 9; // Maximum number of times to restart Kestrel before giving up
    public DateTime LastWork => DateTime.FromBinary(Interlocked.Read(ref Ticks));
    Proxy Proxy;
    public Application Application => Proxy.Application;
    public bool IsDotnet => Application.Assembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
    public IPAddress Loopback => IsDotnet ? IPAddress.IPv6Loopback : IPAddress.Loopback;
    public string LoopbackText => IsDotnet ? "[::1]" : "127.0.0.1";
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
            if (HttpPort > -1) QuicHttpDest = new UdpClient(new IPEndPoint(Loopback, HttpPort));
            if (HttpsPort > -1) QuicHttpsDest = new UdpClient(new IPEndPoint(Loopback, HttpsPort));
        }

        var urls = new StringBuilder();
        if (Proxy.HasHttp) urls.Append($"http://{LoopbackText}:{HttpPort}");
        if (Proxy.HasHttps)
        {
            if (urls.Length > 0) urls.Append(';');
            urls.Append($"https://{LoopbackText}:{HttpsPort}");
        }
        if (Proxy.HasNetTcp)
        {
            if (urls.Length > 0) urls.Append(';');
            urls.Append($"net.tcp://{LoopbackText}:{NetTcpPort}");
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

        info.WorkingDirectory = Application.Assembly.Contains(Path.DirectorySeparatorChar) ?
            Path.GetDirectoryName(Application.Assembly) :
                (OSInfo.IsWindows ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) :
                    (OSInfo.IsMac ? $"/Users/{user}" :
                        (user == "root" ? "/root" : $"/home/{user}")));
        bool dotnet = IsDotnet;
        var arguments = Application.Arguments
            ?.Replace("${httpport}", HttpPort.ToString())
            ?.Replace("${httpsport}", HttpsPort.ToString())
            ?.Replace("${nettcpport}", NetTcpPort.ToString())
            ?.Replace("${loopback}", LoopbackText);
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
    public async Task CopyAsync(TcpClient source, TcpClient destination, CancellationTokenSource cancel)
    {
        if (source == null || destination == null) return;

        var cts = new CancellationTokenSource();
        using (var srcStream = source.GetStream())
        using (var destStream = destination.GetStream())
        using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token, cts.Token))
        {
            // 3) Start bi‑directional copy tasks
            var t1 = Pump(srcStream, destStream, linkedCts.Token); // client → server
            var t2 = Pump(destStream, srcStream, linkedCts.Token); // server → client

            try
            {
                await Task.WhenAny(t1, t2);
                cts.Cancel();
                await Task.WhenAll(t1, t2);
            } catch (Exception ex)
            {
                Logger.LogWarning($"Exception when copying data {ex.Message}");
            }
        }
    }

    static async Task Pump(Stream src, Stream dst, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
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
        catch (OperationCanceledException) { /* expected on shutdown */ }
        catch (IOException) { /* connection reset */ }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}