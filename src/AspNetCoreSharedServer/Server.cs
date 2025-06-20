using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Buffers;

namespace AspNetCoreSharedServer;

public class Server
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
	public static TimeSpan GlobalIdleTimeout = TimeSpan.FromMinutes(5);
	public static TimeSpan GlobalRecycle = TimeSpan.FromMinutes(20);
	public TimeSpan? IdleTimeout = null;
	public TimeSpan? Recycle = null;
	public Receiver? Receiver = null;
	public ILogger Logger => Configuration.Current.Logger;

	public TcpListener Http, Https;
	public UdpClient? QuicHttp, QuicHttps;
	Uri? httpUri = null, httpsUri = null;
	int httpPort = -1, httpsPort = -1;
	public Server(Application app)
	{
		app.Server = this;
		Application = app;
		Assembly = app.Assembly;
		Arguments = app.Arguments ?? string.Empty;
		Environment = app.Environment ?? new Dictionary<string, string>();
		ListenUrls = app.ListenUrls;
		OriginalUrls = app.Urls;
		IdleTimeout = app.IdleTimeout ?? Configuration.Current.IdleTimeout ?? GlobalIdleTimeout;
		Recycle = app.Recycle ?? Configuration.Current.Recycle ?? GlobalRecycle;
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
			if (httpUri.Port == 80) httpPort = Configuration.FindFreePort();
			else httpPort = httpUri.Port;
			actualurls.Add($"http://{IPAddress.Loopback}:{httpPort}");
		}

		if (HasHttps)
		{
			if (httpsUri.Port == 443) httpsPort = Configuration.FindFreePort();
			else httpsPort = httpsUri.Port;
			actualurls.Add($"http://{IPAddress.Loopback}:{httpsPort}");
		}

		Application.ListenUrls = ListenUrls = string.Join(";", actualurls);

		Configuration.Current.SaveIfDirty(true);
	}
	public async Task ListenAsync()
	{
		if (EnableHttp3)
		{
			if (httpPort > -1) QuicHttp = new UdpClient(httpPort);
			if (httpsPort > -1) QuicHttps = new UdpClient(httpsPort);
		}

		Task? t1 = null, t2 = null, t3 = null, t4 = null;
		if (HasHttp)
		{
			Http = new TcpListener(IPAddress.Loopback, httpPort);
			t1 = ListenAsync(Http, receiver => receiver.HttpDest());
		}
		if (HasHttps)
		{
			Http = new TcpListener(IPAddress.Loopback, httpsPort);
			t2 = ListenAsync(Https, receiver => receiver.HttpsDest());
		}
		if (QuicHttp != null) t3 = ListenAsync(QuicHttp, receiver => receiver.QuicHttpDest);
		if (QuicHttps != null) t4 = ListenAsync(QuicHttps, receiver => receiver.QuicHttpsDest);
		
		var tasks = new Task?[] { t1, t2, t3, t4 }
			.Where(t => t != null)
			.Select(t => t!)
			.ToArray();
		await Task.WhenAll(tasks);
	}
	public void Shutdown()
	{
		lock (this)
		{
			if (Receiver != null)
			{
				Receiver.Shutdown();
			}
			Task.Run(async () =>
			{
				await Task.Delay(6000);
				Cancel.Cancel(); // Cancel the main listening loop
			});
		}
	}

	public Receiver StartReceiver()
	{
		lock (this)
		{
			if (Receiver == null) Receiver = new Receiver(this);
			return Receiver;
		}
	}
	public async Task ListenAsync(TcpListener source, Func<Receiver, Task<TcpClient>> destination)
	{
		source.Start(RequestQueueSize);

		Logger.LogInformation($"{Application.Name} listening on {source.LocalEndpoint}");

		while (!Cancel.IsCancellationRequested)
		{
			try
			{
				var client = await source.AcceptTcpClientAsync(Cancel.Token);
				var receiver = StartReceiver();
				var task = receiver.CopyAsync(client, await destination(receiver));
			}
			catch (SocketException ex)
			{
				Console.WriteLine($"Socket error: {ex.Message}");
			}
		}
	}
	public async Task ListenAsync(UdpClient source, Func<Receiver, UdpClient> getDestination)
	{
		UdpClient destination = null;
		Receiver receiver = null;
		while (!Cancel.IsCancellationRequested && receiver?.Cancel.IsCancellationRequested != true)
		{
			var packet = await source.ReceiveAsync();
			if (destination == null)
			{
				lock (this)
				{
					if (Receiver == null) Receiver = new Receiver(this);
					receiver = Receiver;
				}
				destination = getDestination(receiver);
				lock (this)
				{
					if (Receiver == null) Receiver = new Receiver(this);
					receiver = Receiver;
				}
				Task.Run(async () =>
				{
					while (!Cancel.IsCancellationRequested && !receiver.Cancel.IsCancellationRequested)
					{
						var packet = await destination.ReceiveAsync();
						await source.SendAsync(packet.Buffer, packet.Buffer.Length);
					}
				});
				while (!Cancel.IsCancellationRequested && !receiver.Cancel.IsCancellationRequested)
				{
					var packet2 = await source.ReceiveAsync();
					await destination.SendAsync(packet2.Buffer, packet2.Buffer.Length);
				}
			}
		}
	}
}

