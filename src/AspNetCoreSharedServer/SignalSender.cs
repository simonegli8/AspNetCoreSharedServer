using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Diagnostics;
using AspNetCoreSharedServer.Util;

namespace AspNetCoreSharedServer;

public class SignalSender
{

	static readonly object ConsoleLock = new();

	/// <summary>
	/// Sends Ctrl+C to the console of the process, which is how Windows asks a console program (like Kestrel) to shut down. It works for
	/// a process of another user as well, because it only needs a handle on that process. A process has one console, so the server
	/// leaves its own, joins the one of the app, sends the event and goes back; a lock keeps two of these from overlapping, and what
	/// the server itself writes to its console in that moment is lost. Returns false if the console could not be reached, for example
	/// because the process has already exited or has no console.
	/// </summary>
	public static bool SendSigintWindows(int pid)
	{
		lock (ConsoleLock)
		{
			FreeConsole();
			try
			{
				if (!AttachConsole((uint)pid)) return false;
				try
				{
					// The event goes to every process on the console, and the server is one of them while it is attached.
					SetConsoleCtrlHandler(IntPtr.Zero, true);
					var sent = GenerateConsoleCtrlEvent(CTRL_C_EVENT, 0);
					System.Threading.Thread.Sleep(100); // delivery is asynchronous
					return sent;
				}
				finally
				{
					SetConsoleCtrlHandler(IntPtr.Zero, false);
					FreeConsole();
				}
			}
			finally
			{
				AttachConsole(ATTACH_PARENT_PROCESS); // our own console again, if we were started from one
			}
		}
	}

	/// <summary>
	/// Runs <paramref name="start"/>, which creates the process of an app, with Ctrl+C processing switched on for this process. A child
	/// inherits whether Ctrl+C is ignored, and an app that ignores it can never be asked to shut down. The same lock as in
	/// <see cref="SendSigintWindows"/> keeps a launch from overlapping a send, during which this process does ignore Ctrl+C.
	/// </summary>
	public static void StartWithCtrlCEnabled(Action start)
	{
		if (!OSInfo.IsWindows)
		{
			start();
			return;
		}
		lock (ConsoleLock)
		{
			SetConsoleCtrlHandler(IntPtr.Zero, false);
			start();
		}
	}

	const uint CTRL_C_EVENT = 0, ATTACH_PARENT_PROCESS = 0xFFFFFFFF;

	[System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
	static extern bool FreeConsole();

	[System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
	static extern bool AttachConsole(uint processId);

	[System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
	static extern bool SetConsoleCtrlHandler(IntPtr handlerRoutine, bool add);

	[System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
	static extern bool GenerateConsoleCtrlEvent(uint ctrlEvent, uint processGroupId);

	public static void SendSigint(int pid)
	{
		try {
			if (!OSInfo.IsWindows)
			{
				// On Unix-like systems, use the native syscall to send SIGINT
				Unix.kill(pid, Unix.SIGINT);
			} else
			{
				SendSigintWindows(pid);
			}
		} catch (Exception)
		{

		}
	}

	public static void SendSigint(Process process)
	{
		if (process != null && !process.HasExited) SendSigint(process.Id);
	}
}
