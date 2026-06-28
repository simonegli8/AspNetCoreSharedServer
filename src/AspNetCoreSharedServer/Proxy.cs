using EstrellasDeEsperanza.AsyncLock;
using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace AspNetCoreSharedServer;

public class Proxy
{
    public bool EnableHttp3 = true;
    public const int RequestQueueSize = 64;

    public CancellationTokenSource Cancel = new CancellationTokenSource();
    public AsyncLock Lock = new AsyncLock();

    public string Assembly;
    public string Arguments;
    public Application Application = null;
    public string ListenUrls = string.Empty;
    public string OriginalUrls = string.Empty;
    public bool HasHttp = false, HasHttps = false, HasNetTcp = false;
    public Dictionary<string, string> Environment = new Dictionary<string, string>();
    public TimeSpan? IdleTimeout = null;
    public TimeSpan? Recycle = null;
    public Server? Server = null;
    public Queue<DateTime> Failures = new Queue<DateTime>();
    public TimeSpan FailureInterval => Configuration.Current.FailureInterval;
    public int FailureLimit => Configuration.Current.FailureLimit;

    public async Task<bool> FailAsync()
    {
        bool shutdown = false;
        using (await Lock.LockAsync())
        {
            var now = DateTime.UtcNow;
            Failures.Enqueue(now);
            while (now - Failures.Peek() > FailureInterval) Failures.Dequeue();
            shutdown = Failures.Count > FailureLimit;
        }
        if (shutdown)
        {
            await ShutdownAsync(false);
            await Application.SetStatusAsync(Status.Error, "Too many rapid failures of the app pool.", true);
            return true;
        }
        return false;
    }

    public ILogger Logger => Configuration.Current.Logger;

    public List<TcpListener> Http = new List<TcpListener>(), Https = new List<TcpListener>(),
        NetTcp = new List<TcpListener>();
    public UdpClient? QuicHttpV4 = null, QuicHttpsV4 = null, QuicHttpV6 = null, QuicHttpsV6 = null;
    Uri? httpUri = null, httpsUri = null, nettcpUri = null;
    int httpPort = -1, httpsPort = -1, nettcpPort = -1;

    public Proxy(Application app)
    {
        app.Proxy = this;
        Application = app;
        Assembly = app.Assembly;
        Arguments = app.Arguments ?? string.Empty;
        Environment = app.Environment ?? new Dictionary<string, string>();
        ListenUrls = app.ListenUrls;
        OriginalUrls = app.Urls;
        IdleTimeout = app.IdleTimeout ?? Configuration.Current.IdleTimeout ?? Configuration.GlobalIdleTimeout;
        Recycle = app.Recycle ?? Configuration.Current.Recycle ?? Configuration.GlobalRecycle;
        EnableHttp3 = app.EnableHttp3 ?? EnableHttp3;
    }
    public bool HasIPv6 = false;
    public bool HasIPv4 = false;

    public async Task ListenAsync()
    {
        List<string> actualurls = new List<string>();

        foreach (var url in ListenUrls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var uri = new Uri(url);
            if (uri.Scheme == "http")
            {
                HasHttp = true;
                httpUri = uri;
            }
            else if (uri.Scheme == "https")
            {
                HasHttps = true;
                httpsUri = uri;
            }
            else if (uri.Scheme == "net.tcp")
            {
                HasNetTcp = true;
                nettcpUri = uri;
            }
            else throw new ArgumentException("Invalid URL scheme. Only 'http' and 'https' are supported.", nameof(url));
        }
        if (HasHttp)
        {
            if (httpUri.Port == 0)
            {
                httpPort = await Configuration.FindFreePortAsync();
                actualurls.Add($"http://{httpUri.Host}:{httpPort}");
            }
            else
            {
                httpPort = httpUri.Port;
                actualurls.Add(httpUri.ToString());
            }
        }

        if (HasHttps)
        {
            if (httpsUri.Port == 0)
            {
                httpsPort = await Configuration.FindFreePortAsync();
                actualurls.Add($"https://{httpsUri.Host}:{httpsPort}");
            }
            else
            {
                httpsPort = httpsUri.Port;
                actualurls.Add(httpsUri.ToString());
            }
        }

        if (HasNetTcp)
        {
            if (nettcpUri.Port == 0)
            {
                nettcpPort = await Configuration.FindFreePortAsync();
                actualurls.Add($"nettcp://{nettcpUri.Host}:{nettcpPort}");
            }
            else
            {
                nettcpPort = nettcpUri.Port;
                actualurls.Add(nettcpUri.ToString());
            }
        }

        Application.ListenUrls = ListenUrls = string.Join(";", actualurls);

        await Configuration.Current.SaveIfDirtyAsync(true);


        bool exception = false;

        try
        {
            do
            {
                Task? t1 = null, t2 = null, t3 = null, t4 = null, t5 = null, t6 = null;
                if (HasHttp)
                {
                    IPAddress? ip;
                    List<IPAddress> ips = new List<IPAddress>();
                    if (httpUri.Host == "0.0.0.0") ips.Add(IPAddress.Any);
                    else if (httpUri.Host == "[::]") ips.Add(IPAddress.IPv6Any);
                    else if (httpUri.Host == "127.0.0.1") ips.Add(IPAddress.Loopback);
                    else if (httpUri.Host == "[::1]") ips.Add(IPAddress.IPv6Loopback);
                    else if (IPAddress.TryParse(httpUri.Host, out ip)) ips.Add(ip);
                    else
                    {
                        ips.AddRange((await Dns.GetHostAddressesAsync(httpUri.Host))
                            .Distinct());
                    }
                    if (!ips.Any()) ips = new List<IPAddress>() { IPAddress.Any, IPAddress.IPv6Any };
                    Http.Clear();
                    foreach (var ipadr in ips) Http.Add(new TcpListener(ipadr, httpPort));
                    if (ips.Any(ip => ip.AddressFamily == AddressFamily.InterNetwork)) HasIPv4 = true;
                    if (ips.Any(ip => ip.AddressFamily == AddressFamily.InterNetworkV6)) HasIPv6 = true;
                    t1 = ListenAsync(Http, (addressFamily, server) => server.HttpDest(addressFamily));
                }
                if (HasHttps)
                {
                    IPAddress? ip;
                    List<IPAddress> ips = new List<IPAddress>();
                    if (httpsUri.Host == "0.0.0.0") ips.Add(IPAddress.Any);
                    else if (httpsUri.Host == "[::]") ips.Add(IPAddress.IPv6Any);
                    else if (httpsUri.Host == "127.0.0.1") ips.Add(IPAddress.Loopback);
                    else if (httpsUri.Host == "[::1]") ips.Add(IPAddress.IPv6Loopback);
                    else if (IPAddress.TryParse(httpsUri.Host, out ip)) ips.Add(ip);
                    else
                    {
                        ips.AddRange((await Dns.GetHostAddressesAsync(httpsUri.Host))
                            .Distinct());
                    }
                    if (!ips.Any()) ips = new List<IPAddress>() { IPAddress.Any, IPAddress.IPv6Any };
                    if (ips.Any(ip => ip.AddressFamily == AddressFamily.InterNetwork)) HasIPv4 = true;
                    if (ips.Any(ip => ip.AddressFamily == AddressFamily.InterNetworkV6)) HasIPv6 = true;
                    Https.Clear();
                    foreach (var ipadr in ips) Https.Add(new TcpListener(ipadr, httpsPort));

                    t2 = ListenAsync(Https, (addressFamily, server) => server.HttpsDest(addressFamily));
                }
                if (HasNetTcp)
                {
                    IPAddress? ip;
                    List<IPAddress> ips = new List<IPAddress>();
                    if (nettcpUri.Host == "0.0.0.0") ips.Add(IPAddress.Any);
                    else if (nettcpUri.Host == "[::]") ips.Add(IPAddress.IPv6Any);
                    else if (nettcpUri.Host == "127.0.0.1") ips.Add(IPAddress.Loopback);
                    else if (nettcpUri.Host == "[::1]") ips.Add(IPAddress.IPv6Loopback);
                    else if (IPAddress.TryParse(nettcpUri.Host, out ip)) ips.Add(ip);
                    else
                    {
                        ips.AddRange((await Dns.GetHostAddressesAsync(nettcpUri.Host))
                            .Distinct());
                    }
                    if (!ips.Any()) ips = new List<IPAddress>() { IPAddress.Any, IPAddress.IPv6Any };
                    if (ips.Any(ip => ip.AddressFamily == AddressFamily.InterNetwork)) HasIPv4 = true;
                    if (ips.Any(ip => ip.AddressFamily == AddressFamily.InterNetworkV6)) HasIPv6 = true;
                    NetTcp.Clear();
                    foreach (var ipadr in ips) NetTcp.Add(new TcpListener(ipadr, nettcpPort));

                    t1 = ListenAsync(NetTcp, (addressFamily, server) => server.NetTcpDest(addressFamily));
                }

                if (EnableHttp3)
                {
                    if (httpPort > -1)
                    {
                        if (HasIPv4) QuicHttpV4 = new UdpClient(httpPort, AddressFamily.InterNetwork);
                        if (HasIPv6) QuicHttpV6 = new UdpClient(httpPort, AddressFamily.InterNetworkV6);
                    }
                    if (httpsPort > -1)
                    {
                        if (HasIPv4) QuicHttpsV4 = new UdpClient(httpsPort, AddressFamily.InterNetwork);
                        if (HasIPv6) QuicHttpsV6 = new UdpClient(httpsPort, AddressFamily.InterNetworkV6);
                    }

                    if (QuicHttpV4 != null) t3 = ListenAsync(QuicHttpV4, server => server.QuicHttpDestV4);
                    if (QuicHttpsV4 != null) t4 = ListenAsync(QuicHttpsV4, server => server.QuicHttpsDestV4);
                    if (QuicHttpV6 != null) t3 = ListenAsync(QuicHttpV6, server => server.QuicHttpDestV6);
                    if (QuicHttpsV6 != null) t4 = ListenAsync(QuicHttpsV6, server => server.QuicHttpsDestV6);
                }
                var tasks = new Task?[] { t1, t2, t3, t4, t5, t6 }
                    .Where(t => t != null)
                    .Select(t => t!)
                    .ToArray();
                await Task.WhenAll(tasks);
            } while (!Cancel.IsCancellationRequested);
        }
        catch (OperationCanceledException ex)
        {
        }
        catch (SocketException ex)
        {
            Logger.LogError(ex, $"{Application.Name}: Socket error while starting server");
            exception = true;
            await Application.SetStatusAsync(Status.Error, $"Socket error while starting server {ex}", true);
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, $"{Application.Name}: Error while starting server");
            exception = true;
            await Application.SetStatusAsync(Status.Error, $"Error while starting server {ex}", true);
            throw;
        }
        finally
        {
            /*if (!exception)
            {
                Server = StartServer();
                Logger.LogInformation($"{Application.Name} started listening on {ListenUrls}");
            }*/
        }
    }

    public async Task DetachAsync()
    {
        using var mylock = await Lock.LockAsync();
        if (Server != null)
        {
            Server.UdpCancel.Cancel();
            Server = null;
        }
    }

    public async Task ShutdownAsync(bool waitOnServerShutdown = true)
    {
#if Server
        Logger.LogInformation($"Shutdown {Application.Name}.");
        using var mylock = await Lock.LockAsync();
        if (Server != null)
        {
            await Server.ShutdownAsync(true, waitOnServerShutdown);
            Server = null;
        }
#endif
    }
    public void Shutdown(bool wait = true)
    {
        throw new NotSupportedException();
    }

    public async Task<Server> StartServerAsync()
    {
        using var mylock = await Lock.LockAsync();
        if (!Cancel.IsCancellationRequested)
        {
            if (Server == null)
            {
                Server = new Server(this);
                await Server.StartAsync();
                //Debug.WriteLine($"Started server {Server.ProcessId} for {Application.Name}");
            }
        }
        return Server;
    }
    public async Task ListenAsync(List<TcpListener> sources, Func<AddressFamily, Server, Task<TcpClient>> destination)
    {
        await Task.WhenAll(sources.Select(source => ListenAsync(source, destination)));
    }
    public async Task ListenAsync(TcpListener source, Func<AddressFamily, Server, Task<TcpClient>> destination)
    {
        try
        {
            source.Start(RequestQueueSize);
            Logger.LogInformation($"{Application.Name} listening on {source.LocalEndpoint}");
        }
        catch (Exception ex)
        {
            Logger.LogError($"{Application.Name} listening on {source.LocalEndpoint} failed: {ex.Message}");
            throw;
        }

        while (!Cancel.IsCancellationRequested)
        {
            try
            {
                var client = await source.AcceptTcpClientAsync(Cancel.Token);
                if (!Cancel.IsCancellationRequested)
                {
                    var server = await StartServerAsync();
                    if (server != null && !Cancel.IsCancellationRequested && !server.TcpCancel.IsCancellationRequested)
                    {
                        _ = server.CopyAsync(client, await destination(client.Client.AddressFamily, server), server.TcpCancel);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                if (Cancel.IsCancellationRequested) break;
            }
            catch (SocketException ex)
            {
                Logger.LogError(ex, $"{Application.Name}: Socket error while accepting TCP client");
                await Application.SetStatusAsync(Status.Error, $"Socket error while accepting TCP client {ex}", true);
            }
        }
        source.Stop();
        //Logger.LogInformation($"{Application.Name}: Listening stopped.");
    }
    public async Task ListenAsync(UdpClient source, Func<Server, UdpClient> getDestination)
    {
        try
        {
            while (!Cancel.IsCancellationRequested)
            {
                try
                {
                    var packet = await source.ReceiveAsync(Cancel.Token);
                    var server = await StartServerAsync();
                    if (server != null)
                    {
                        using var destination = getDestination(server);
                        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Cancel.Token, server.UdpCancel.Token);
                        var receiver = Task.Run(async () =>
                        {
                            while (!cancel.IsCancellationRequested)
                            {
                                try
                                {
                                    var packet = await destination.ReceiveAsync(cancel.Token);
                                    await source.SendAsync(new ReadOnlyMemory<byte>(packet.Buffer, 0, packet.Buffer.Length), cancel.Token);
                                }
                                catch { }
                            }
                        });
                        await destination.SendAsync(new ReadOnlyMemory<byte>(packet.Buffer, 0, packet.Buffer.Length), cancel.Token);
                        while (!cancel.IsCancellationRequested)
                        {
                            try
                            {
                                var packet2 = await source.ReceiveAsync(cancel.Token);
                                await destination.SendAsync(new ReadOnlyMemory<byte>(packet2.Buffer, 0, packet2.Buffer.Length), cancel.Token);
                            }
                            catch { }
                        }
                        await receiver;
                    }
                }
                catch { }
            }
        }
        finally
        {
            source.Close();
            source.Dispose();
        }
    }
}
