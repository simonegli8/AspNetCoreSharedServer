using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace AspNetCoreSharedServer;


[Flags]
public enum OSPlatform { Unknown = 0, None = 0, Windows = 1, Unix = 2, Mac = 4, Linux = 8, Other = 0x10, All = 0x1F };
public enum OSFlavor { Unknown = 0, Min = 0, Windows, Mac, Debian, Mint, Kali, Ubuntu, Fedora, RedHat, Oracle, CentOS, Alma, Rocky, SUSE, Alpine, Arch, FreeBSD, NetBSD, Other, Max = Other }

public class OSInfo
{
	public static bool IsMono => Type.GetType("Mono.Runtime") != null;
	public static bool IsCore => !(IsNetFX || IsNetNative);
	public static bool IsNetFX => RuntimeInformation.FrameworkDescription.StartsWith(".NET Framework", StringComparison.OrdinalIgnoreCase);
	public static bool IsNetNative => RuntimeInformation.FrameworkDescription.StartsWith(".NET Native", StringComparison.OrdinalIgnoreCase);
	public static bool IsWindows => RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows);
	public static bool IsLinux => RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Linux);
	public static bool IsMac => RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.OSX);
	public static bool IsArm => Architecture == Architecture.Arm64 || Architecture == Architecture.Arm;
	public static bool IsIntel => Architecture == Architecture.X64 || Architecture == Architecture.X86;
	public static bool IsNet48 => !IsMono && IsWindows && Regex.IsMatch(RuntimeInformation.FrameworkDescription, @"^\.NET Framework 4\.[8-9]");
	public static bool Is64 => Environment.Is64BitOperatingSystem;
	public static bool Is32 => !Is64;
	public static string FrameworkDescription => RuntimeInformation.FrameworkDescription;

	public static readonly System.Runtime.InteropServices.OSPlatform FreeBSD = System.Runtime.InteropServices.OSPlatform.Create("FREEBSD");
	public static readonly System.Runtime.InteropServices.OSPlatform NetBSD = System.Runtime.InteropServices.OSPlatform.Create("NETBSD");

	public static bool IsUnix => IsLinux || IsMac || IsFreeBSD || IsNetBSD;
	public static bool IsFreeBSD => RuntimeInformation.IsOSPlatform(FreeBSD);
	public static bool IsNetBSD => RuntimeInformation.IsOSPlatform(NetBSD);
	public static bool IsSystemd => IsLinux && Directory.Exists("/run/systemd/system");
#if Server
	public static bool IsOpenRC => IsLinux && File.Exists("/etc/rc.conf") && Services.Shell.Standard.Find("rc-status") != null;
#endif
	public static bool IsWSL => IsLinux && File.Exists("/proc/version") && File.ReadAllText("/proc/version").ToLower().Contains("microsoft");
        public static bool IsLinuxMusl
        {
            get
            {
                if (!IsLinux) return false;

                var info = new ProcessStartInfo("ldd");
                info.Arguments = "/bin/ls";
                info.RedirectStandardOutput = true;
                info.UseShellExecute = false;
                var p = Process.Start(info);
                return p.StandardOutput.ReadToEnd().Contains("musl");
            }
        }

        static OSFlavor flavor = OSFlavor.Unknown;
	static Version version = new Version("0.0.0.0");

	public static OSPlatform OSPlatform => IsWindows ? OSPlatform.Windows :
		 (IsMac ? OSPlatform.Mac :
		 (IsLinux ? OSPlatform.Linux :
		 (IsNetBSD || IsFreeBSD ? OSPlatform.Unix : OSPlatform.Other)));

	public static Architecture Architecture => RuntimeInformation.ProcessArchitecture;
#if Server
	public static OSFlavor OSFlavor
	{
		get
		{
			if (flavor != OSFlavor.Unknown) return flavor;
			version = Environment.OSVersion.Version;
			if (IsWindows) return OSFlavor.Windows;
			if (IsMac) return OSFlavor.Mac;
			if (IsFreeBSD) return OSFlavor.FreeBSD;
			if (IsNetBSD) return OSFlavor.NetBSD;
			if (IsLinux)
			{
				string name = null;
				const string OsReleaseFile = "/etc/os-release";
				if (File.Exists(OsReleaseFile))
				{
					var osRelease = File.ReadAllText(OsReleaseFile);
					var match = Regex.Match(osRelease, "(?<=^NAME\\s*=\\s*\")[^\"]+(?=\")", RegexOptions.IgnoreCase | RegexOptions.Multiline);
					if (match.Success) name = match.Value;
					match = Regex.Match(osRelease, @"(?<=^VERSION_ID\s*=\s*""?[^""0-9]*?)[0-9]+(\.[0-9]+)?(\.[0-9]+)?(\.[0-9]+)?", RegexOptions.IgnoreCase | RegexOptions.Multiline);
					Version osReleaseVersion;
					int intVersion;
					if (match.Success)
					{
						if (Version.TryParse(match.Value, out osReleaseVersion)) version = osReleaseVersion;
						else if (int.TryParse(match.Value, out intVersion)) {
							version = new Version(intVersion, 0);
						}
					}
				}
				if (name == null)
				{
					var osRelease = Services.Shell.Standard.Exec("lsb_release -a").Output().Result;
					var match = Regex.Match(osRelease, "(?<=^Distributor ID\\s*:\\s*)[^\\s$]+(?=\\s|$)", RegexOptions.IgnoreCase | RegexOptions.Multiline);
					if (match.Success) name = match.Value;
					match = Regex.Match(osRelease, @"(?<=^Release\s*:[^0-9]*?)[0-9]+\.[0-9]+(\.[0-9]+)?(\.[0-9]+)?", RegexOptions.IgnoreCase | RegexOptions.Multiline);
					if (match.Success) Version.TryParse(match.Value, out version);
				}
				// TODO use hostnamectl
				OSFlavor f;
				if (name == null) flavor = OSFlavor.Other;
				else
				{
					if (name != "Linux" && name != "linux" && name.EndsWith("linux", StringComparison.OrdinalIgnoreCase))
					{
						name = name.Substring(0, name.Length - "linux".Length);
					}
					if (Enum.TryParse<OSFlavor>(name, out f)) flavor = f;
					else
					{
						for (var os = OSFlavor.Min; os <= OSFlavor.Max; os++)
						{
							if (Regex.IsMatch(name, $"(?<=^|\\s){Regex.Escape(Enum.GetName(typeof(OSFlavor), os))}(?=\\s|$)", RegexOptions.IgnoreCase))
							{
								flavor = os;
								break;
							}
                            }
                            for (var os = OSFlavor.Min; os <= OSFlavor.Max; os++)
                            {
                                if (name.IndexOf(Enum.GetName(typeof(OSFlavor), os)) >= 0)
                                {
                                    flavor = os;
                                    break;
                                }
                            }
                        }
                    }
			}
			return flavor == OSFlavor.Unknown ? OSFlavor.Other : flavor;
		}
	}
	public static Version OSVersion
	{
		get
		{
			var flavor = OSFlavor;
			return version;
		}
	}
#endif

}
