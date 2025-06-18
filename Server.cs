using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Buffers;

namespace AspNetCoreServer;

public class Server
{
	public const int RequestQueueSize = 64;
	public const int StartPort = Receiver.StartPort;
	public const int EndPort = Receiver.EndPort;
	public static int Port
	{
		get => Receiver.Port;
		set => Receiver.Port = value;
	}

	public CancellationTokenSource Cancel = new CancellationTokenSource();

	public string Assembly;
	public string Arguments;
	public Application Application = null;
	public string ListenUrls = string.Empty;
	public string OriginalUrls = string.Empty;
	public Dictionary<string, string> Environment = new Dictionary<string, string>();
	public static TimeSpan GlobalIdleTimeout = TimeSpan.FromMinutes(5);
	public static TimeSpan GlobalRecycle = TimeSpan.FromMinutes(20);
	public TimeSpan IdleTimeout = GlobalIdleTimeout;
	public TimeSpan Recycle =GlobalRecycle;
	public Receiver? Receiver = null;

	public Socket? Http, Https, QuicHttp, QuicHttps;
	public Server(Application app)
	{
		app.Server = this;
		Application = app;
		Assembly = app.Assembly;
		Arguments = app.Arguments ?? string.Empty;
		Environment = app.Environment ?? new Dictionary<string, string>();
		ListenUrls = app.ListenUrls;
		OriginalUrls = app.Urls;
		IdleTimeout = app.IdleTimeout != TimeSpan.Zero ? app.IdleTimeout : GlobalIdleTimeout;
		Recycle = app.Recycle != TimeSpan.Zero ? app.Recycle : GlobalRecycle;
	}
	public void Listen()
	{
		Uri? httpUri = null, httpsUri = null;
		List<string> actualurls = new List<string>();

		foreach (var url in ListenUrls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			var uri = new Uri(url);
			if (uri.Scheme == "http") httpUri = uri;
			else if (uri.Scheme == "https") httpsUri = uri;
			else throw new ArgumentException("Invalid URL scheme. Only 'http' and 'https' are supported.", nameof(url));
		}

		Http = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
		QuicHttp = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Udp);
		Https = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
		QuicHttps = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Udp);

		if (httpUri != null)
		{
			if (httpUri.Host == "unix")
			{
				Http.Bind(new UnixDomainSocketEndPoint(httpUri.AbsolutePath));
				actualurls.Add(httpUri.ToString());
			}
			else
			{
				if (httpUri.Port == 80)
				{
					do
					{
						try
						{
							Http.Bind(new IPEndPoint(IPAddress.Loopback, Port));
							QuicHttp.Bind(new IPEndPoint(IPAddress.Loopback, Port));
							actualurls.Add($"http://{IPAddress.Loopback}:{Port}");
							Port++;
							if (Port > EndPort) Port = StartPort;
							break;
						}
						catch
						{
							Port++;
							if (Port > EndPort) Port = StartPort;
						}
					} while (true);
				}
				else
				{
					Http.Bind(new IPEndPoint(IPAddress.Any, httpUri.Port));
					QuicHttp.Bind(new IPEndPoint(IPAddress.Any, httpUri.Port));
					actualurls.Add($"http://{IPAddress.Loopback}:{httpUri.Port}");
				}
			}
		}

		if (httpsUri != null)
		{
			if (httpsUri.Host == "unix")
			{
				Https.Bind(new UnixDomainSocketEndPoint(httpsUri.AbsolutePath));
				actualurls.Add(httpsUri.ToString());
			}
			else
			{
				if (httpsUri.Port == 443)
				{
					do
					{
						try
						{
							Https.Bind(new IPEndPoint(IPAddress.Any, Port));
							QuicHttps.Bind(new IPEndPoint(IPAddress.Any, Port));
							actualurls.Add($"http://{IPAddress.Loopback}:{Port}");
							Port++;
							if (Port > EndPort) Port = StartPort;
							break;
						}
						catch
						{
							Port++;
							if (Port > EndPort) Port = StartPort;
						}
					} while (true);
				}
				else
				{
					Https.Bind(new IPEndPoint(IPAddress.Any, httpsUri.Port));
					QuicHttps.Bind(new IPEndPoint(IPAddress.Any, httpsUri.Port));
					actualurls.Add($"http://{IPAddress.Loopback}:{httpUri.Port}");
				}
			}
		}

		Application.ListenUrls = ListenUrls = string.Join(";", actualurls);

		if (Http != null) Task.Run(async () => await ListenAsync(Http, receiver => receiver.HttpDest));
		if (Https != null) Task.Run(async () => await ListenAsync(Https, receiver => receiver.HttpsDest));
		if (QuicHttp != null) Task.Run(async () => await ListenAsync(QuicHttp, receiver => receiver.QuicHttpDest));
		if (QuicHttps != null) Task.Run(async () => await ListenAsync(QuicHttps, receiver => receiver.QuicHttpsDest));
	}
	public void Shutdown()
	{
		lock (this)
		{
			if (Receiver != null)
			{
				Receiver.Shutdown();
			}
		}
	}

	public async Task ListenAsync(Socket source, Func<Receiver, Socket> destination)
	{
		source.Listen(RequestQueueSize);

		while (!Cancel.IsCancellationRequested)
		{
			try
			{
				var client = await source.AcceptAsync();
				Receiver receiver;
				lock (this)
				{
					if (Receiver == null) Receiver = new Receiver(this);
					receiver = Receiver;
				}

				Task.Run(async () => await receiver.CopyAsync(client, destination(receiver)));
			}
			catch (SocketException ex)
			{
				Console.WriteLine($"Socket error: {ex.Message}");
			}
		}
	}
}

