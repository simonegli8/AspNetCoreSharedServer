using NeoSmart.AsyncLock;
using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
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

    public bool Fail()
    {
        bool shutdown = false;
        lock (Lock)
        {
            var now = DateTime.UtcNow;
            Failures.Enqueue(now);
            while (now - Failures.Peek() > FailureInterval) Failures.Dequeue();
            shutdown = Failures.Count > FailureLimit;
        }
        if (shutdown)
        {
            Shutdown();
            Application.SetStatus(Status.Error, "Too many rapid failures of the app pool.", true);
            return true;
        }
        return false;
    }
    public ILogger Logger => Configuration.Current.Logger;

    public List<TcpListener> Http = new List<TcpListener>(), Https = new List<TcpListener>(),
        NetTcp = new List<TcpListener>();
    public UdpClient? QuicHttp, QuicHttps;
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
                httpPort = Configuration.FindFreePort();
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
                httpsPort = Configuration.FindFreePort();
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
                nettcpPort = Configuration.FindFreePort();
                actualurls.Add($"nettcp://{nettcpUri.Host}:{nettcpPort}");
            }
            else
            {
                nettcpPort = nettcpUri.Port;
                actualurls.Add(nettcpUri.ToString());
            }
        }

        Application.ListenUrls = ListenUrls = string.Join(";", actualurls);

        Configuration.Current.SaveIfDirty(true);
    }
    public async Task ListenAsync()
    {
        bool exception = false;
        try
        {
            do
            {
                if (EnableHttp3)
                {
                    if (httpPort > -1) QuicHttp = new UdpClient(httpPort);
                    if (httpsPort > -1) QuicHttps = new UdpClient(httpsPort);
                }

                Task? t1 = null, t2 = null, t3 = null, t4 = null;
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

                    t1 = ListenAsync(Http, server => server.HttpDest());
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
                    Https.Clear();
                    foreach (var ipadr in ips) Https.Add(new TcpListener(ipadr, httpsPort));

                    t2 = ListenAsync(Https, server => server.HttpsDest());
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
                    NetTcp.Clear();
                    foreach (var ipadr in ips) NetTcp.Add(new TcpListener(ipadr, nettcpPort));

                    t1 = ListenAsync(NetTcp, server => server.NetTcpDest());
                }
                if (QuicHttp != null) t3 = ListenAsync(QuicHttp, server => server.QuicHttpDest);
                if (QuicHttps != null) t4 = ListenAsync(QuicHttps, server => server.QuicHttpsDest);

                var tasks = new Task?[] { t1, t2, t3, t4 }
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
            Application.SetStatus(Status.Error, $"Socket error while starting server {ex}", true);
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, $"{Application.Name}: Error while starting server");
            exception = true;
            Application.SetStatus(Status.Error, $"Error while starting server {ex}", true);
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
    public void Shutdown()
    {
        Logger.LogInformation($"Shutdown {Application.Name}.");

        lock (this)
        {
            if (Server != null)
            {
                Server.Shutdown();
            }
            Cancel.Cancel(); // Cancel the main listening loop
        }
    }

    public Server StartServer()
    {
        using var mylock = Lock.Lock();
        if (!Cancel.IsCancellationRequested)
        {
                
            if (Server == null)
            {
                Server = new Server(this);
                Server.CheckStarted();
                //Debug.WriteLine($"Started server {Server.ProcessId} for {Application.Name}");
            }
        }
        return Server;
    }
    public async Task ListenAsync(List<TcpListener> sources, Func<Server, Task<TcpClient>> destination)
    {
        await Task.WhenAll(sources.Select(source => ListenAsync(source, destination)));
    }
    public async Task ListenAsync(TcpListener source, Func<Server, Task<TcpClient>> destination)
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
                    var server = StartServer();
                    if (server != null && !Cancel.IsCancellationRequested)
                    {
                        var task = server.CopyAsync(client, await destination(server), Cancel);
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
                Application.SetStatus(Status.Error, $"Socket error while accepting TCP client {ex}", true);
            }
        }
        source.Stop();
        //Logger.LogInformation($"{Application.Name}: Listening stopped.");
    }
    public async Task ListenAsync(UdpClient source, Func<Server, UdpClient> getDestination)
    {
        UdpClient destination = null;
        Server server = null;
        try
        {
            while (!Cancel.IsCancellationRequested)
            {
                try
                {
                    var packet = await source.ReceiveAsync(Cancel.Token);
                    if (destination == null)
                    {
                        server = StartServer();
                        if (server != null)
                        {
                            destination = getDestination(server);
                            /*lock (this)
                            {
                                if (!Cancel.IsCancellationRequested)
                                {
                                    if (Server == null) Server = new Server(this);
                                }
                                server = Server;
                            }*/
                            _ = Task.Run(async () =>
                            {
                                while (!Cancel.IsCancellationRequested)
                                {
                                    try {
                                        var packet = await destination.ReceiveAsync(Cancel.Token);
                                        await source.SendAsync(new ReadOnlyMemory<byte>(packet.Buffer, 0, packet.Buffer.Length), Cancel.Token);
                                    } catch { break; }
                                }
                            });
                            await destination.SendAsync(new ReadOnlyMemory<byte>(packet.Buffer, 0, packet.Buffer.Length), Cancel.Token);
                            while (!Cancel.IsCancellationRequested)
                            {
                                try {
                                    var packet2 = await source.ReceiveAsync(Cancel.Token);
                                    await destination.SendAsync(new ReadOnlyMemory<byte>(packet2.Buffer, 0, packet2.Buffer.Length), Cancel.Token);
                                } catch { break; }
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    if (Cancel.IsCancellationRequested) break;
                }
            }
        }
        finally
        {
            source.Close();
            destination?.Close();
            source.Dispose();
            destination?.Dispose();
        }
    }
}

