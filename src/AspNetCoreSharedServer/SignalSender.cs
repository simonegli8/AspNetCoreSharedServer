using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Diagnostics;
using Mono.Unix.Native;

namespace AspNetCoreSharedServer;

public class SignalSender
{

	public static void SendSigintWindows(int pid)
	{
		var signaler = Type.GetType("Medallion.Shell.Signals.WindowsProcessSignaler, MedallionShell");
		if (signaler == null) throw new InvalidOperationException("MedallionShell is not available. Please install the Medallion.Shell package to use this feature.");
		var ctrlType = Type.GetType("Medallion.Shell.Signals.NativeMethods+CtrlType, MedallionShell");
		var sendSigintMethod = signaler
			.GetMethod("TrySignalAsync");
		if (sendSigintMethod == null) throw new InvalidOperationException("MedallionShell does not have the required method. Please ensure you are using a compatible version.");
		sendSigintMethod?.Invoke(null, new object[] { pid, Enum.ToObject(ctrlType, 0)});
	}
	public static void SendSigint(int pid)
	{
		try {
			if (Environment.OSVersion.Platform != PlatformID.Win32NT)
			{
				// On Unix-like systems, use the native syscall to send SIGINT
				Syscall.kill(pid, Signum.SIGINT);
			} else
			{
				SendSigintWindows(pid);
			}
		} catch (Exception ex)
		{

		}
	}

	public static void SendSigint(Process process)
	{
		if (process != null && !process.HasExited) SendSigint(process.Id);
	}
}
