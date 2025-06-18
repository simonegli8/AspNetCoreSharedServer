
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Buffers;

namespace AspNetCoreServer;

public class Receiver
{
	public const int BufferSize = 4 * 1024;
	public const int StartPort = 10000;
	public const int EndPort = 50000;
	public static int Port = StartPort;

	public Socket HttpDest, HttpsDest, QuicHttpDest, QuicHttpsDest;
	public long Ticks = 0;
	public DateTime Started = DateTime.Now;
	public DateTime LastWork => DateTime.FromBinary(Interlocked.Read(ref Ticks));
	Server Server;
	Process? Kestrel;

	public Receiver(Server server)
	{
		Server = server;
		if (!Directory.Exists("/run/aspnet")) Directory.CreateDirectory("/run/aspnet");

		var ticks = DateTime.Now.ToBinary();
		HttpDest = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true, SendTimeout = 1000, ReceiveTimeout = 1000 };
		HttpsDest = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true, SendTimeout = 1000, ReceiveTimeout = 1000 };
		QuicHttpDest = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { NoDelay = true, SendTimeout = 1000, ReceiveTimeout = 1000 };
		QuicHttpsDest = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { NoDelay = true, SendTimeout = 1000, ReceiveTimeout = 1000 };

		int httpPort, httpsPort;
		do {
			try
			{
				HttpDest.Bind(new IPEndPoint(IPAddress.Loopback, Port));
				QuicHttpDest.Bind(new IPEndPoint(IPAddress.Loopback, Port));
				httpPort = Port++;
				if (Port > EndPort) Port = StartPort;
				break;
			} catch {
				Port++;
				if (Port > EndPort) Port = StartPort;
			}
		} while (true);

		do
		{
			try
			{
				HttpsDest.Bind(new IPEndPoint(IPAddress.Loopback, Port));
				QuicHttpsDest.Bind(new IPEndPoint(IPAddress.Loopback, Port));
				httpsPort = Port++;
				if (Port > EndPort) Port = StartPort;
				break;
			}
			catch
			{
				Port++;
				if (Port > EndPort) Port = StartPort;
			}
		} while (true);

		var info = new ProcessStartInfo("dotnet");
		info.WorkingDirectory = Path.GetDirectoryName(Server.Assembly);
		info.Arguments = $"\"{Server.Assembly}\" {Server.Arguments}";
		info.CreateNoWindow = true;
		foreach (var key in Server.Environment.Keys)
		{
			info.Environment[key] = Server.Environment[key];
		}
		info.Environment["ORIGINAL_URLS"] = Server.OriginalUrls;
		info.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{httpPort};https://127.0.0.1{httpsPort}";
		info.RedirectStandardError = info.RedirectStandardOutput = false;
		info.RedirectStandardInput = true;
		info.UseShellExecute = false;
		info.WindowStyle = ProcessWindowStyle.Hidden;
		Kestrel = Process.Start(info);

		Ticks = DateTime.Now.ToBinary();

		if (Server.Recycle != TimeSpan.Zero && Server.IdleTimeout != TimeSpan.Zero)
			Task.Run(async () => await CheckTimeout());
	}
	public void Shutdown()
	{
		if (Kestrel != null)
		{
			if (!Kestrel.HasExited)
			{
				Kestrel.StandardInput.Write('\u001A'); // Send Ctrl+Z to gracefully stop Kestrel
				lock (Server) Server.Receiver = null; // Clear the receiver reference to prevent further processing
			}
			Kestrel = null;
		}
	}

	public async Task CheckTimeout()
	{
		do
		{
			var now = DateTime.Now;
			if (Server.IdleTimeout != TimeSpan.Zero && now - LastWork > Server.IdleTimeout ||
				Server.Recycle != TimeSpan.Zero && now - Started > Server.Recycle)
			{
				Shutdown();
				return;
			}
			await Task.Delay(5000);
		} while (Server.Cancel.IsCancellationRequested == false);
		
		if (Server.Cancel.IsCancellationRequested) Shutdown();
	}
	public async Task CopyAsync(Socket source, Socket destination)
	{
		if (source == null || destination == null) return;

		byte[] sendbuffer = ArrayPool<byte>.Shared.Rent(BufferSize);
		byte[] receivebuffer = ArrayPool<byte>.Shared.Rent(BufferSize);
		try
		{
			Task.Run(async () =>
			{
				try
				{
					int bytesRead = 1;
					// receive
					while (bytesRead != 0)
					{
						bytesRead = await destination.ReceiveAsync(new ArraySegment<byte>(receivebuffer), SocketFlags.None);
						Interlocked.Exchange(ref Ticks, DateTime.Now.ToBinary());
						await source.SendAsync(new ArraySegment<byte>(receivebuffer, 0, bytesRead), SocketFlags.None);
					}
				}
				catch (Exception ex)
				{
					Console.WriteLine($"Error processing client {destination.RemoteEndPoint}: {ex.Message}");
				}
				finally
				{
					ArrayPool<byte>.Shared.Return(receivebuffer);
					source.Close();
				}
			});
			// send
			int bytesRead = 1;
			while (bytesRead != 0)
			{
				bytesRead = await source.ReceiveAsync(new ArraySegment<byte>(sendbuffer), SocketFlags.None);
				Interlocked.Exchange(ref Ticks, DateTime.Now.ToBinary());
				await destination.SendAsync(new ArraySegment<byte>(sendbuffer, 0, bytesRead), SocketFlags.None);
			}
		}
		catch (Exception ex)
		{
			Console.WriteLine($"Error copying data: {ex.Message}");
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(sendbuffer);
			destination.Close();
			//source.Close();
			Console.WriteLine("Connection closed.");
		}
	}
}