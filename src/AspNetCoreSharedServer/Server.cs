
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Buffers;

namespace AspNetCoreSharedServer;

public class Server
{
	public const int BufferSize = 4 * 1024;
	public ILogger Logger => Configuration.Current.Logger;
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
	public async Task<TcpClient> HttpDest() {
		return await Connect(HttpPort);
	}
	public async Task<TcpClient> HttpsDest()
	{
		return await Connect(HttpsPort);
	}
	public int HttpPort = -1, HttpsPort = -1;
	public UdpClient QuicHttpDest, QuicHttpsDest;
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
	Process ? Kestrel;
	public CancellationTokenSource Cancel = new CancellationTokenSource();

	public Server(Proxy server)
	{
		Proxy = server;
		//if (!Directory.Exists("/run/aspnet")) Directory.CreateDirectory("/run/aspnet");

		if (Proxy.HasHttp) HttpPort = Configuration.FindFreePort(true);
		if (Proxy.HasHttps) HttpsPort = Configuration.FindFreePort(true);

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
		var info = new ProcessStartInfo();
		info.WorkingDirectory = Path.GetDirectoryName(Application.Assembly);
		var user = Application.User ?? Configuration.Current.User;
		var group = Application.Group ?? Configuration.Current.Group;
		bool dotnet = IsDotnet;
		var arguments = Application.Arguments?.Replace("${httpport}", HttpPort.ToString())
			?.Replace("${httpsport}", HttpsPort.ToString());
		if (!OSInfo.IsWindows && Mono.Unix.Native.Syscall.getuid() == 0 &&
			!string.IsNullOrEmpty(user))
		{
			string groupArg = "";
			if (!string.IsNullOrEmpty(group)) groupArg = $"-g  {group} ";
			// If running as root, use sudo to drop privileges
			var env = $"ASPNETCORE_URLS={urls} ORIGINAL_URLS={Application.Urls ?? ""} ";
			info.FileName = "sudo";
			info.Arguments = $"-u {user} {groupArg}{env}-- {(dotnet ? "dotnet " : "")}\"{Application.Assembly}\"{(string.IsNullOrEmpty(arguments) ? "" : " " + arguments)}";
		}
		else
		{
			// Otherwise, run dotnet directly
			if (dotnet)
			{
				info.FileName = "dotnet";
				info.Arguments = $"\"{Application.Assembly}\"{(!string.IsNullOrEmpty(arguments) ? "" : " " + arguments)}";
			} else
			{  // Assembly is AOT
				info.FileName = Application.Assembly;
				info.Arguments = arguments ?? "";
			}
		}
		info.CreateNoWindow = false;
		foreach (var key in Application.Environment.Keys)
		{
			info.Environment[key] = Application.Environment[key];
		}
		info.Environment["ORIGINAL_URLS"] = Application.Urls ?? "";
		info.Environment["ASPNETCORE_URLS"] = urls.ToString();
		Logger.LogInformation($"Starting {Application.Name} on {urls}");
		info.RedirectStandardError = info.RedirectStandardOutput = false;
		info.RedirectStandardInput = true;
		info.UseShellExecute = false;
		info.WindowStyle = ProcessWindowStyle.Normal;
		Logger.LogInformation($"{(!string.IsNullOrEmpty(user) ? user : "")}>{info.FileName} {info.Arguments}");

		Kestrel = Process.Start(info);

		Ticks = DateTime.Now.ToBinary();
		
		if (Kestrel.HasExited) Logger.LogError($"{Application.Name}: Failed to start application.");

		if (Proxy.Recycle != null || Proxy.IdleTimeout != null ||
			Proxy.Recycle != TimeSpan.Zero || Proxy.IdleTimeout != TimeSpan.Zero)
			Task.Run(async () => await CheckTimeout());
	}
	bool shuttingDown = false;
	public void Shutdown()
	{
		Logger.LogInformation($"Shutting down {Application.Name}.");

		if (Kestrel != null)
		{
			shuttingDown = true;

			if (!Kestrel.HasExited) SignalSender.SendSigint(Kestrel); // Send SIGINT to gracefully stop Kestrel
			Kestrel = null;

			lock (Proxy) Proxy.Server = null; // Clear the server reference to prevent further processing
			Task.Run(async () =>
			{
				await Task.Delay(6000);
				Cancel.Cancel();
			});
			
		}
	}

	public async Task CheckTimeout()
	{
		do
		{
			var now = DateTime.Now;
			if (Proxy.IdleTimeout != null && Proxy.IdleTimeout != TimeSpan.Zero &&
				now - LastWork > Proxy.IdleTimeout ||
				Proxy.Recycle != null && Proxy.Recycle != TimeSpan.Zero &&
				now - Started > Proxy.Recycle)
			{
				Logger.LogInformation($"Shutdown {Application.Name}.");
				Shutdown();
				if (Proxy.IdleTimeout == TimeSpan.Zero)
				{
					// Immediately restart when IdleTimeout is set to zero
					Proxy.StartServer();
				}
				return;
			} else if (Kestrel != null && Kestrel.HasExited && !shuttingDown) // If Kestrel has stopped, restart it
			{
				KestrelRestarts++;
				if (KestrelRestarts > MaxKestrelRestarts)
				{
					Logger.LogError($"Kestrel has exited too many times ({KestrelRestarts}), giving up on restarting.");
					Shutdown();
					return;
				}
				else
				{
					Proxy.Server = null;
					Proxy.StartServer();
				}
			}
			await Task.Delay(5000);
		} while (Proxy.Cancel.IsCancellationRequested == false);
		
		if (Proxy.Cancel.IsCancellationRequested) Shutdown();
	}
	public async Task CopyAsync(TcpClient source, TcpClient destination)
	{
		if (source == null || destination == null) return;

		using (var srcStream = source.GetStream())
		using (var destStream = destination.GetStream())
		{
			// 3) Start bi‑directional copy tasks
			var cts = new CancellationTokenSource();
			var t1 = Pump(srcStream, destStream, cts.Token); // client → server
			var t2 = Pump(destStream, srcStream, cts.Token); // server → client

			await Task.WhenAny(t1, t2);
			cts.Cancel();
			await Task.WhenAll(t1.ContinueWith(t => { }), t2.ContinueWith(t => { }));
		}
	}

	static async Task Pump(Stream src, Stream dst, CancellationToken ct)
	{
		var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
		try
		{
			while (true)
			{
				int read = await src.ReadAsync(buffer, 0, buffer.Length, ct);
				if (read == 0)
					break;          // remote closed
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