
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

public class Receiver
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
				await client.ConnectAsync(new IPEndPoint(IPAddress.IPv6Loopback, port), cancel.Token);
			}
			catch (Exception ex)
			{
			}
			while (!client.Connected)
			{
				await Task.Delay(10, cancel.Token);
				try
				{
					await client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), cancel.Token);
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
	Server Server;
	public Application Application => Server.Application;
	Process? Kestrel;
	public CancellationTokenSource Cancel = new CancellationTokenSource();

	public Receiver(Server server)
	{
		Server = server;
		//if (!Directory.Exists("/run/aspnet")) Directory.CreateDirectory("/run/aspnet");

		if (Server.HasHttp) HttpPort = Configuration.FindFreePort(true);
		if (Server.HasHttps) HttpsPort = Configuration.FindFreePort(true);

		if (Server.EnableHttp3)
		{
			if (HttpPort > -1) QuicHttpDest = new UdpClient(new IPEndPoint(IPAddress.IPv6Loopback, HttpPort));
			if (HttpsPort > -1) QuicHttpsDest = new UdpClient(new IPEndPoint(IPAddress.IPv6Loopback, HttpsPort));
		}

		var info = new ProcessStartInfo();
		info.WorkingDirectory = Path.GetDirectoryName(Application.Assembly);
		if (Environment.OSVersion.Platform != PlatformID.Win32NT && Mono.Unix.Native.Syscall.getuid() == 0 &&
			!string.IsNullOrEmpty(Application.User))
		{
			string groupArg = "";
			if (!string.IsNullOrEmpty(Application.Group)) groupArg = $"-g  {Application.Group} ";
			// If running as root, use sudo to drop privileges
			info.FileName = "sudo";
			info.Arguments = $"-E -u {Application.User} {groupArg}-- dotnet \"{Server.Assembly}\"{(!string.IsNullOrEmpty(Server.Arguments) ? "" : " " + Server.Arguments)}";
		}
		else
		{
			// Otherwise, run dotnet directly
			info.FileName = "dotnet";
			info.Arguments = $"\"{Application.Assembly}\"{(!string.IsNullOrEmpty(Application.Arguments) ? "" : " " + Server.Arguments)}";
		}
		info.CreateNoWindow = false;
		var urls = new StringBuilder();
		if (Server.HasHttp) urls.Append($"http://[::1]:{HttpPort}");
		if (Server.HasHttps)
		{
			if (urls.Length > 0) urls.Append(';');
			urls.Append($"https://[::1]:{HttpsPort}");
		}
		foreach (var key in Application.Environment.Keys)
		{
			info.Environment[key] = Application.Environment[key];
		}
		info.Environment["ORIGINAL_URLS"] = Application.Urls ?? "";
		info.Environment["ASPNETCORE_URLS"] = urls.ToString();
		Logger.LogInformation($"Starting Kestrel on {urls}");
		info.RedirectStandardError = info.RedirectStandardOutput = false;
		info.RedirectStandardInput = true;
		info.UseShellExecute = false;
		info.WindowStyle = ProcessWindowStyle.Normal;
		Kestrel = Process.Start(info);

		Ticks = DateTime.Now.ToBinary();
		
		if (Kestrel.HasExited) Logger.LogError($"{Application.Name}: Failed to start Kestrel.");

		if (Server.Recycle != null || Server.IdleTimeout != null ||
			Server.Recycle != TimeSpan.Zero || Server.IdleTimeout != TimeSpan.Zero)
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

			lock (Server) Server.Receiver = null; // Clear the receiver reference to prevent further processing
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
			if (Server.IdleTimeout != null && Server.IdleTimeout != TimeSpan.Zero &&
				now - LastWork > Server.IdleTimeout ||
				Server.Recycle != null && Server.Recycle != TimeSpan.Zero &&
				now - Started > Server.Recycle)
			{
				Logger.LogInformation($"Shutdown {Application.Name}.");
				Shutdown();
				if (Server.IdleTimeout == TimeSpan.Zero)
				{
					// Immediately restart when IdleTimeout is set to zero
					Server.StartReceiver();
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
					Server.Receiver = null;
					Server.StartReceiver();
				}
			}
			await Task.Delay(5000);
		} while (Server.Cancel.IsCancellationRequested == false);
		
		if (Server.Cancel.IsCancellationRequested) Shutdown();
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