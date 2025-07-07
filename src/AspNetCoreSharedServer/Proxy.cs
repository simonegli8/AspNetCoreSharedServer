using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Buffers;

namespace AspNetCoreSharedServer;

public class Proxy
{
	public bool EnableHttp3 = true;
	public const int RequestQueueSize = 64;

	public CancellationTokenSource Cancel = new CancellationTokenSource();

	public string Assembly;
	public string Arguments;
	public Application Application = null;
	public string ListenUrls = string.Empty;
	public string OriginalUrls = string.Empty;
	public bool HasHttp = false, HasHttps = false;
	public Dictionary<string, string> Environment = new Dictionary<string, string>();
	public TimeSpan? IdleTimeout = null;
	public TimeSpan? Recycle = null;
	public Server? Server = null;
	public ILogger Logger => Configuration.Current.Logger;

	public List<TcpListener> Http = new List<TcpListener>(), Https = new List<TcpListener>();
	public UdpClient? QuicHttp, QuicHttps;
	Uri? httpUri = null, httpsUri = null;
	int httpPort = -1, httpsPort = -1;
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
			else throw new ArgumentException("Invalid URL scheme. Only 'http' and 'https' are supported.", nameof(url));
		}
		if (HasHttp)
		{
			if (httpUri.Port == 80)
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
			if (httpsUri.Port == 443)
			{
				httpsPort = Configuration.FindFreePort();
				actualurls.Add($"http://{httpsUri.Host}:{httpsPort}");
			}
			else
			{
				httpsPort = httpsUri.Port;
				actualurls.Add(httpsUri.ToString());
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
			if (QuicHttp != null) t3 = ListenAsync(QuicHttp, server => server.QuicHttpDest);
			if (QuicHttps != null) t4 = ListenAsync(QuicHttps, server => server.QuicHttpsDest);

			var tasks = new Task?[] { t1, t2, t3, t4 }
				.Where(t => t != null)
				.Select(t => t!)
				.ToArray();
			await Task.WhenAll(tasks);
		}
		catch (OperationCanceledException ex)
		{
		}
		catch (SocketException ex)
		{
			Logger.LogError(ex, $"{Application.Name}: Socket error while starting server");
			Configuration.Current.ReportError($"{Application.Name}: Socket error while starting server", ex);
			exception = true;
			throw;
		}
		catch (Exception ex)
		{
			Logger.LogError(ex, $"{Application.Name}: Error while starting server");
			Configuration.Current.ReportError($"{Application.Name}: Socket error while starting server", ex);
			exception = true;
			throw;
		}
		finally
		{
			if (!exception)
			{
				Server = StartServer();
				Logger.LogInformation($"{Application.Name} started listening on {ListenUrls}");
			}
		}
	}
	public void Shutdown()
	{
		lock (this)
		{
			if (Server != null)
			{
				Server.Shutdown();
			}
			Task.Run(async () =>
			{
				await Task.Delay(6000);
				Cancel.Cancel(); // Cancel the main listening loop
			});
		}
	}

	public Server StartServer()
	{
		lock (this)
		{
			if (Server == null) Server = new Server(this);
			return Server;
		}
	}
	public async Task ListenAsync(List<TcpListener> sources, Func<Server, Task<TcpClient>> destination)
	{
		foreach (var source in sources.ToArray())
		{
			try
			{
				source.Start(RequestQueueSize);
				Logger.LogInformation($"{Application.Name} listening on {source.LocalEndpoint}");
			}
			catch (Exception ex)
			{
				sources.Remove(source);
				Logger.LogError($"{Application.Name} listening on {source.LocalEndpoint} failed:{System.Environment.NewLine}{ex}");
			}
		}

		while (!Cancel.IsCancellationRequested)
		{
			try
			{
				var client = await Task.WhenAny<TcpClient>(sources.Select(async src => await src.AcceptTcpClientAsync(Cancel.Token)));
				var server = StartServer();
				var task = server.CopyAsync(await client, await destination(server));
			}
			catch (SocketException ex)
			{
				Logger.LogError(ex, "Socket error while accepting TCP client");
				Configuration.Current.ReportError("Socket error while accepting TCP client", ex);
			}
		}
	}
	public async Task ListenAsync(UdpClient source, Func<Server, UdpClient> getDestination)
	{
		UdpClient destination = null;
		Server server = null;
		while (!Cancel.IsCancellationRequested && server?.Cancel.IsCancellationRequested != true)
		{
			var packet = await source.ReceiveAsync();
			if (destination == null)
			{
				lock (this)
				{
					if (Server == null) Server = new Server(this);
					server = Server;
				}
				destination = getDestination(server);
				lock (this)
				{
					if (Server == null) Server = new Server(this);
					server = Server;
				}
				Task.Run(async () =>
				{
					while (!Cancel.IsCancellationRequested && !server.Cancel.IsCancellationRequested)
					{
						var packet = await destination.ReceiveAsync();
						await source.SendAsync(packet.Buffer, packet.Buffer.Length);
					}
				});
				while (!Cancel.IsCancellationRequested && !server.Cancel.IsCancellationRequested)
				{
					var packet2 = await source.ReceiveAsync();
					await destination.SendAsync(packet2.Buffer, packet2.Buffer.Length);
				}
			}
		}
	}
}

