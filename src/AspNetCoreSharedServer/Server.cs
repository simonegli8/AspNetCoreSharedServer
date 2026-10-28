
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
    public ILogger Logger => (ILogger?)Configuration.Current.Logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    public CancellationTokenSource TcpCancel = new CancellationTokenSource();
    public CancellationTokenSource UdpCancel = new CancellationTokenSource();

    const int ConnectTimeoutMs = 30000;
    /// <summary>
    /// Connects to the app on loopback, retrying until the app accepts connections. Throws OperationCanceledException
    /// when the timeout elapses or this server is cancelled (e.g. its process exited). Returns null if the port is not in use.
    /// </summary>
    public async Task<TcpClient?> Connect(int port, AddressFamily family)
    {
        if (port <= -1) return null;

        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TcpCancel.Token);
        cancel.CancelAfter(ConnectTimeoutMs);

        var endpoints = family == AddressFamily.InterNetworkV6 ?
            new[] { new IPEndPoint(IPAddress.IPv6Loopback, port) } :
            new[] { new IPEndPoint(IPAddress.Loopback, port), new IPEndPoint(IPAddress.IPv6Loopback, port) };

        while (true)
        {
            foreach (var endpoint in endpoints)
            {
                // A socket cannot reliably be reused after a failed connect, so every attempt gets a fresh TcpClient.
                var client = new TcpClient(endpoint.AddressFamily);
                try
                {
                    await client.ConnectAsync(endpoint, cancel.Token);
                    return client;
                }
                catch (Exception ex)
                {
                    client.Dispose();
                    if (ex is OperationCanceledException) throw;
                }
            }
            await Task.Delay(10, cancel.Token);
        }
    }
    public async Task<TcpClient?> HttpDest(AddressFamily family)
    {
        return await Connect(HttpPort, family);
    }
    public async Task<TcpClient?> HttpsDest(AddressFamily family)
    {
        return await Connect(HttpsPort, family);
    }
    public async Task<TcpClient?> NetTcpDest(AddressFamily family)
    {
        return await Connect(NetTcpPort, family);
    }
    public int HttpPort = -1, HttpsPort = -1, NetTcpPort = -1;
    public DateTime Started = DateTime.UtcNow;
    public long Ticks = DateTime.UtcNow.ToBinary();
    const int MaxKestrelRestarts = 9; // Maximum number of times to restart Kestrel before giving up
    public DateTime LastWork => DateTime.FromBinary(Interlocked.Read(ref Ticks));
    Proxy Proxy;
    public Application Application => Proxy.Application;
    public bool IsDotnet => Application.Assembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
    public const string LoopbackTextV4 = "127.0.0.1";
    public const string LoopbackTextV6 = "[::1]";
    Process ServerProcess = new Process();
    //public CancellationTokenSource Cancel = new CancellationTokenSource();

    public virtual string? Find(string cmd) => Services.Shell.Standard.Find(cmd);

    public Server(Proxy proxy)
    {
        Proxy = proxy;
    }

    /// <summary>Starts the app process. Returns false if it could not be started.</summary>
    public async Task<bool> StartAsync()
    {
        // Validate before allocating any ports or sockets.
        if (!File.Exists(Application.Assembly))
        {
            return await FailStartAsync($"Assembly {Application.Assembly} not found.", "Assembly not found.");
        }

        if (Proxy.HasHttp) HttpPort = await Configuration.FindFreePortAsync(true);
        // With HTTP/3 the app's QUIC listener needs the same number as its HTTPS port, free on UDP as well.
        if (Proxy.HasHttps) HttpsPort = await Configuration.FindFreePortAsync(true, alsoUdp: Proxy.Http3Active);
        if (Proxy.HasNetTcp) NetTcpPort = await Configuration.FindFreePortAsync(true);

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
        var altSvc = CreateAltSvcHeader(Proxy.Http3Active, Proxy.HasHttps, Proxy.PublicHttpsPort);
        var proxyProtocol = CreateProxyProtocolVersion(Application.EnableProxyHeader, Application.EnableProxyV2Header);

        // The environment of the app: its own configuration, then the variables the server defines, which an app cannot override.
        var variables = CreateEnvironment(Application.Environment, Application.Urls, urls.ToString(), altSvc, proxyProtocol);

        // What to run: dotnet <assembly> <arguments>, or an AOT executable.
        string program, programArguments;
        if (dotnet)
        {
            program = Find("dotnet") ?? "dotnet";
            programArguments = $"{QuoteArg(Application.Assembly)}{(string.IsNullOrEmpty(arguments) ? "" : " " + arguments)}";
        }
        else
        {  // Assembly is AOT
            program = Application.Assembly;
            programArguments = arguments ?? "";
        }

        if (!OSInfo.IsWindows && Unix.IsRoot && !string.IsNullOrEmpty(user))
        {
            // A typo in the user or group would otherwise show up as sudo failing and the app exiting at once.
            if (!Unix.UserExists(user))
            {
                return await FailStartAsync($"User {user} not found.", $"User {user} not found.");
            }
            if (!string.IsNullOrEmpty(group) && !Unix.GroupExists(group))
            {
                return await FailStartAsync($"Group {group} not found.", $"Group {group} not found.");
            }

            // Drop privileges with sudo, on Linux and on macOS alike. The app becomes the other user completely (real, effective
            // and saved ids). Switching the effective user of this process around Process.Start would not: the real user stays root,
            // and the app could switch back to it.
            info.FileName = "sudo";
            info.Arguments = CreateSudoArguments(user, group, variables,
                $"{QuoteArg(program)}{(programArguments.Length > 0 ? " " + programArguments : "")}", Logger);
        }
        else
        {
            info.FileName = program;
            info.Arguments = programArguments;
            foreach (var variable in variables) info.Environment[variable.Key] = variable.Value;
        }

        info.CreateNoWindow = false;
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

        ServerProcess.Start();

        ServerProcess.BeginErrorReadLine();
        ServerProcess.BeginOutputReadLine();

        Touch();
        isRunning = true;

        if (Proxy.Recycle != null && Proxy.Recycle != TimeSpan.Zero || 
            Proxy.IdleTimeout != null && Proxy.IdleTimeout != TimeSpan.Zero ||
            Configuration.Current.IdleTimeoutOnLowMemory != TimeSpan.Zero)
            _ = Task.Run(async () => await CheckTimeoutAsync());

        await CheckStartedAsync();
        // CheckStartedAsync clears Proxy.Server if the process exited right away.
        return Proxy.Server == this;
    }

    /// <summary>Environment variable holding the ready-made Alt-Svc header value for the app to send. Empty if HTTP/3 is not forwarded.</summary>
    public const string AltSvcVariable = "HTTP3_ALT_SVC_HEADER";
    /// <summary>Environment variable that is "true" when HTTP/3 is forwarded to the app, otherwise "false".</summary>
    public const string Http3EnabledVariable = "HTTP3_ENABLED";

    /// <summary>The environment variable in which the server tells an app that it runs behind AspNetCoreSharedServer, and which version.</summary>
    public const string ServerVariable = "ASPNET_SERVER";

    /// <summary>The version of AspNetCoreSharedServer, as <c>major.minor.build</c> like <c>aspnet-server -v</c> prints it.</summary>
    public static string Version { get; } = typeof(Server).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
    const int AltSvcMaxAgeSeconds = 86400;

    /// <summary>
    /// Environment variable that tells the app that every TCP connection to it starts with a PROXY protocol header, and of which
    /// version: "v1" or "v2". It is not set when the server sends no such header.
    /// </summary>
    public const string ProxyProtocolVariable = "PROXY_PROTOCOL";

    /// <summary>
    /// "v2" or "v1" if the server sends a PROXY protocol header at the start of every TCP connection to the app, otherwise an empty
    /// string. When both are enabled the server sends v2. This is the one place that decides it, for the header that is sent and for
    /// the environment variable that announces it, so that the two cannot disagree.
    /// </summary>
    public static string CreateProxyProtocolVersion(bool? enableProxyHeader, bool? enableProxyV2Header) =>
        enableProxyV2Header == true ? "v2" : enableProxyHeader == true ? "v1" : "";

    /// <summary>
    /// The environment of an app: the variables of its own configuration, then the ones the server defines, which an app cannot override.
    /// <see cref="ProxyProtocolVariable"/> is only there when the server sends a PROXY header (<paramref name="proxyProtocol"/> is not
    /// empty); a value from the configuration of the app is removed, because it would claim something that is not true.
    /// </summary>
    internal static Dictionary<string, string> CreateEnvironment(IReadOnlyDictionary<string, string>? appEnvironment, string? originalUrls,
        string urls, string altSvc, string proxyProtocol)
    {
        var variables = new Dictionary<string, string>();
        if (appEnvironment != null)
        {
            foreach (var variable in appEnvironment) variables[variable.Key] = variable.Value ?? "";
        }
        variables["ORIGINAL_URLS"] = originalUrls ?? "";
        variables["ASPNETCORE_URLS"] = urls;
        variables[ServerVariable] = Version;
        variables[AltSvcVariable] = altSvc;
        variables[Http3EnabledVariable] = altSvc.Length > 0 ? "true" : "false";
        variables.Remove(ProxyProtocolVariable);
        if (proxyProtocol.Length > 0) variables[ProxyProtocolVariable] = proxyProtocol;
        return variables;
    }

    /// <summary>
    /// The Alt-Svc header value that advertises HTTP/3 on the proxy's public HTTPS port, or an empty string when HTTP/3 is not forwarded.
    /// Kestrel only knows its own internal port and would advertise that one, which clients cannot reach, so the app has to send this
    /// value itself.
    /// </summary>
    public static string CreateAltSvcHeader(bool http3Enabled, bool hasHttps, int publicHttpsPort) =>
        http3Enabled && hasHttps && publicHttpsPort > 0 ? $"h3=\":{publicHttpsPort}\"; ma={AltSvcMaxAgeSeconds}" : "";

    static readonly System.Text.RegularExpressions.Regex VariableName = new("^[A-Za-z_][A-Za-z0-9_]*$");

    /// <summary>
    /// The sudo command line (everything after "sudo") that runs <paramref name="command"/> as another user: <c>-H -u user [-g group]
    /// NAME=value... -- command</c>. The command must already be quoted. Every value in front of "--" is quoted here, so that none of
    /// them can add sudo options or change the command.
    /// </summary>
    /// <param name="environment">
    /// The environment of the app. sudo resets the environment, so the variables travel on its command line. USER and LOGNAME are
    /// added for the target user unless the environment defines them.
    /// </param>
    internal static string CreateSudoArguments(string user, string? group, IReadOnlyDictionary<string, string> environment, string command, ILogger? logger = null)
    {
        // -H makes sudo set HOME to the home directory of the target user. sudo reads the password database itself; the layout of
        // a passwd record differs between Linux and macOS, so the server does not read it.
        var args = new List<string> { "-H", "-u", QuoteArg(user) };
        if (!string.IsNullOrEmpty(group))
        {
            args.Add("-g");
            args.Add(QuoteArg(group));
        }

        var variables = new Dictionary<string, string>(environment);
        if (!user.StartsWith('#')) // "#1001" names the user by number, so there is no name to put into USER
        {
            variables.TryAdd("USER", user);
            variables.TryAdd("LOGNAME", user);
        }
        foreach (var variable in variables)
        {
            if (!VariableName.IsMatch(variable.Key))
            {
                logger?.LogWarning("Ignoring environment variable with invalid name '{Name}'.", variable.Key);
                continue;
            }
            args.Add(QuoteArg($"{variable.Key}={variable.Value}"));
        }
        return $"{string.Join(" ", args)} -- {command}";
    }

    /// <summary>Quotes a single argument for a ProcessStartInfo.Arguments string, so it is parsed back as exactly one argument.</summary>
    internal static string QuoteArg(string value)
    {
        var sb = new StringBuilder("\"");
        int backslashes = 0;
        foreach (var c in value)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }
            if (c == '"') sb.Append('\\', backslashes * 2 + 1).Append('"');
            else sb.Append('\\', backslashes).Append(c);
            backslashes = 0;
        }
        return sb.Append('\\', backslashes * 2).Append('"').ToString();
    }

    /// <summary>
    /// Aborts a start that never launched a process: clears the proxy's reference, releases the sockets
    /// and marks the app as failed. Must be called while the proxy lock is held (as StartAsync is).
    /// </summary>
    async Task<bool> FailStartAsync(string logMessage, string status)
    {
        Logger.LogError(logMessage);
        if (Proxy.Server == this) Proxy.Server = null;
        Release();
        await Application.SetStatusAsync(Status.Error, status, true);
        return false;
    }

    /// <summary>Cancels this server's TCP copies and UDP sessions. Safe to call more than once.</summary>
    public void Release()
    {
        TcpCancel.Cancel();
        UdpCancel.Cancel();
    }

    /// <summary>Records that traffic flowed, so that the app does not count as idle.</summary>
    public void Touch() => Interlocked.Exchange(ref Ticks, DateTime.UtcNow.ToBinary());

    /// <summary>Set once the app's process has been started.</summary>
    volatile bool isRunning;
    UdpBackend? udpBackend;

    /// <summary>
    /// Where HTTP/3 datagrams for this app go: its loopback HTTPS port. Null while the app is not running (or has no HTTPS
    /// listener), because a UDP datagram must never be the reason that an app is started.
    /// </summary>
    public UdpBackend? GetUdpBackend()
    {
        if (!isRunning || HttpsPort <= 0 || UdpCancel.IsCancellationRequested) return null;
        return udpBackend ??= new UdpBackend(HttpsPort, UdpCancel.Token, Touch);
    }

    private async Task OnExitAsync()
    {
        try
        {
            bool fail = false;

            try
            {
                await Proxy.DetachAsync(this);
            }
            finally
            {
                TcpCancel.Cancel();
            }
            if (!shuttingDown)
            {
                fail = await Proxy.FailAsync();
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed OnExit");
        }
    }

    public async Task OnOutputAsync(string? data, TextWriter writer)
    {
        try
        {
            if (data != null)
            {
                if (data.Contains(ShutdownMessage))
                {
                    await Proxy.DetachAsync(this);
                }
                writer.WriteLine(data);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed OnOutput");
        }
    }

    public async Task CheckStartedAsync()
    {
        if (ServerProcess?.HasExited != false)
        {
            if (Proxy.Server == this) Proxy.Server = null;
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
                if (Proxy.Server == this) Proxy.Server = null; // Clear the server reference to prevent further processing
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
                var start = DateTime.UtcNow;
                do
                {
                    await Task.Delay(100);
                } while (!ServerProcess.HasExited && DateTime.UtcNow - start < timeout);

                if (!ServerProcess.HasExited) ServerProcess.Kill();

                TcpCancel.Cancel();
            }
        }
    }

    public int ProcessId => ServerProcess?.Id ?? 0;

    static readonly AsyncLock MemoryLock = new AsyncLock();
    static HardwareInfo? sharedHardwareInfo = null;
    static double cachedUsedMemory = 0;
    static long cachedUsedMemoryAt = 0;
    static bool hasCachedUsedMemory = false;
    const int MemoryCacheMs = 1000;

    /// <summary>
    /// Fraction of physical memory in use. Shared by all servers and refreshed at most once per second, so that
    /// the number of running apps does not multiply the cost of polling the hardware.
    /// </summary>
    static async Task<double> GetUsedMemoryAsync()
    {
        using (var @lock = await MemoryLock.LockAsync())
        {
            var now = Environment.TickCount64;
            if (!hasCachedUsedMemory || now - cachedUsedMemoryAt >= MemoryCacheMs)
            {
                // Created on first use: constructing HardwareInfo is expensive, and must not run in a type initializer.
                sharedHardwareInfo ??= new HardwareInfo();
                sharedHardwareInfo.RefreshMemoryStatus();

                ulong total = sharedHardwareInfo.MemoryStatus.TotalPhysical;
                ulong available = sharedHardwareInfo.MemoryStatus.AvailablePhysical;
                // Unknown total: report no memory pressure, instead of NaN.
                cachedUsedMemory = total == 0 ? 0 : (double)(total - available) / (double)total;
                cachedUsedMemoryAt = now;
                hasCachedUsedMemory = true;
            }
            return cachedUsedMemory;
        }
    }

    public async Task CheckTimeoutAsync()
    {
        do
        {
            try
            {
                var now = DateTime.UtcNow;
                var usedmem = await GetUsedMemoryAsync();

                var muchmem = usedmem < Configuration.Current.MemoryLowThreshold;
                var idleTimeout = muchmem ?
                    Proxy.IdleTimeout :
                    Configuration.Current.IdleTimeoutOnLowMemory ?? Configuration.DefaultIdleTimeoutOnLowMemory;
                if (Proxy.IdleTimeout != TimeSpan.Zero && Proxy.IdleTimeout < idleTimeout) idleTimeout = Proxy.IdleTimeout;

                if ((Proxy.IdleTimeout != null && Proxy.IdleTimeout != TimeSpan.Zero ||
                    (!muchmem && idleTimeout != TimeSpan.Zero)) &&
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
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error in CheckTimeout");
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
        if (destinationAddress.IsIPv4MappedToIPv6) destinationAddress = destinationAddress.MapToIPv4();

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
    /// <summary>
    /// Builds the PROXY protocol header for a connection accepted from a client. The header describes the client's
    /// connection to the proxy: the client is the source, and the address and port the client connected to is the
    /// destination. (Not the proxy's own socket to the app, which would be an ephemeral loopback port.)
    /// </summary>
    public static byte[] CreateProxyHeader(TcpClient source, bool v2)
    {
        var clientEp = (IPEndPoint)source.Client.RemoteEndPoint!;
        var serverEp = (IPEndPoint)source.Client.LocalEndPoint!;
        var clientAddress = NormalizeAddress(clientEp.Address);
        var serverAddress = NormalizeAddress(serverEp.Address);

        if (clientAddress.AddressFamily != serverAddress.AddressFamily)
            throw new NotSupportedException("Proxy does not support IPv4 and IPv6 mix.");

        if (v2) return CreateProxyV2Header(clientAddress, serverAddress, (ushort)clientEp.Port, (ushort)serverEp.Port, ProtocolType.Tcp);

        // Standard PROXY v1 line: PROXY TCP4/6 [Source_IP] [Dest_IP] [Source_Port] [Dest_Port]\r\n
        var version = clientAddress.AddressFamily == AddressFamily.InterNetworkV6 ? "TCP6" : "TCP4";
        return Encoding.ASCII.GetBytes($"PROXY {version} {clientAddress} {serverAddress} {clientEp.Port} {serverEp.Port}\r\n");
    }

    /// <summary>Unwraps IPv4-mapped IPv6 addresses (dual-mode sockets) and drops IPv6 scope ids, which PROXY does not allow.</summary>
    static IPAddress NormalizeAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) return address.MapToIPv4();
        if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId != 0) return new IPAddress(address.GetAddressBytes());
        return address;
    }

    public async Task CopyAsync(TcpClient source, TcpClient destination, CancellationTokenSource cancel)
    {
        if (source == null || destination == null) return;

        using (var srcStream = source.GetStream())
        using (var destStream = destination.GetStream())
        {
            // Send proxy header
            var proxyProtocol = CreateProxyProtocolVersion(Application.EnableProxyHeader, Application.EnableProxyV2Header);
            if (proxyProtocol.Length > 0)
            {
                var v2 = proxyProtocol == "v2";
                var proxyHeader = CreateProxyHeader(source, v2);
                // Inject the PROXY header
                await destStream.WriteAsync(proxyHeader, 0, proxyHeader.Length, cancel.Token);
                Logger.LogInformation(v2 ? "Sent PROXY v2 header" : "Sent PROXY v1 header");
            }

            // Start bi‑directional copy tasks
            try
            {
                await RelayAsync(srcStream, destStream, source.Client, destination.Client, cancel.Token, Touch);
            }
            catch (Exception ex)
            {
                Logger?.LogWarning($"Exception when copying data {ex.Message}");
            }
        }
    }

    /// <summary>
    /// How long a half-closed connection may stay silent in its remaining direction before it is torn down.
    /// Without a bound, a peer that vanished after its half-close would hold the connection forever.
    /// </summary>
    public static TimeSpan HalfCloseIdleTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Copies data in both directions between a client and the app. When one side finishes sending (EOF), that is passed
    /// on as a half-close and the other direction keeps running until it finishes too, so a reply is never cut off
    /// because the request was half-closed. A reset or cancellation tears down both directions at once.
    /// </summary>
    public static async Task RelayAsync(NetworkStream client, NetworkStream backend, Socket clientSocket, Socket backendSocket,
        CancellationToken cancel, Action? onActivity = null)
    {
        using var abort = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        long lastActivity = Environment.TickCount64;
        void Activity()
        {
            Volatile.Write(ref lastActivity, Environment.TickCount64);
            onActivity?.Invoke();
        }

        var pending = new List<Task<bool>>
        {
            Pump(client, backend, backendSocket, Activity, abort.Token), // client → server
            Pump(backend, client, clientSocket, Activity, abort.Token),  // server → client
        };
        try
        {
            while (pending.Count > 0)
            {
                var finished = await Task.WhenAny(pending);
                pending.Remove(finished);
                if (!await finished)
                {
                    abort.Cancel(); // reset or cancelled: tear down the other direction as well
                }
                else if (pending.Count > 0)
                {
                    // One direction ended cleanly. Let the other finish, unless its peer stays silent too long.
                    var rest = pending[0];
                    while (!rest.IsCompleted && !abort.IsCancellationRequested)
                    {
                        using var wait = CancellationTokenSource.CreateLinkedTokenSource(abort.Token);
                        await Task.WhenAny(rest, Task.Delay(HalfCloseIdleTimeout, wait.Token));
                        wait.Cancel(); // release the delay timer
                        var idle = TimeSpan.FromMilliseconds(Environment.TickCount64 - Volatile.Read(ref lastActivity));
                        if (!rest.IsCompleted && idle >= HalfCloseIdleTimeout) abort.Cancel();
                    }
                }
            }
        }
        finally
        {
            abort.Cancel(); // never leave a pump running behind when leaving early
        }
    }

    /// <returns>True if the sender finished cleanly (EOF, passed on as a half-close); false if the copy was reset or cancelled.</returns>
    static async Task<bool> Pump(NetworkStream src, NetworkStream dst, Socket dstSocket, Action onData, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int read = await src.ReadAsync(buffer, 0, buffer.Length, ct);
                if (read == 0)
                {
                    // The sender is done. Pass the FIN on, but keep the other direction open so the reply is not cut off.
                    try { dstSocket.Shutdown(SocketShutdown.Send); }
                    catch (SocketException) { } // the peer already closed
                    catch (ObjectDisposedException) { }
                    return true;
                }

                onData();

                await dst.WriteAsync(buffer, 0, read, ct);
                await dst.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        return false;
    }
}