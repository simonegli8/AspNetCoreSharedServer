using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace AspNetCoreSharedServer
{
	internal class OSInfo
	{
		public static bool IsWindows => RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows);
		public static bool IsLinux => RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Linux);
		public static bool IsMac => RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.OSX);
	}
}
