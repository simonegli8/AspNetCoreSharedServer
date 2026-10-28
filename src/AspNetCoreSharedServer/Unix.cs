using AspNetCoreSharedServer.Util;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace AspNetCoreSharedServer;


[System.FlagsAttribute]
public enum UnixFileMode
{
    None = 0,
    OtherExecute = 1,
    OtherWrite = 2,
    OtherRead = 4,
    GroupExecute = 8,
    GroupWrite = 0x10,
    GroupRead = 0x20,
    UserExecute = 0x40,
    UserWrite = 0x80,
    UserRead = 0x100,
    StickyBit = 0x200,
    SetGroup = 0x400,
    SetUser = 0x800,
    All = 0x1ff
}

public class Unix
{
    [DllImport("libc", SetLastError = true)]
    public static extern int chmod(string path, uint mode);

    // There is deliberately no seteuid/setegid here. Switching the effective user of the server around Process.Start leaves the real
    // user as root, so the started app can switch back to root. Apps are started as another user with sudo, see Server.

    /// <summary>
    /// The start of a passwd record. Only these four fields are the same on every platform: Linux continues with pw_gecos, pw_dir
    /// and pw_shell, macOS with pw_change and pw_class, so the fields behind these are deliberately not declared.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Passwd
    {
        public IntPtr pw_name;
        public IntPtr pw_passwd;
        public uint pw_uid;
        public uint pw_gid;
    }

    [DllImport("libc")]
    public static extern IntPtr getpwnam(string name);

    [StructLayout(LayoutKind.Sequential)]
    public struct Group
    {
        public IntPtr gr_name;
        public IntPtr gr_passwd;
        public uint gr_gid;
        public IntPtr gr_mem; // char** (array of strings)
    }

    [DllImport("libc")]
    public static extern IntPtr getgrnam(string name);

    /// <summary>"#1001" is how sudo names a numeric id. sudo resolves it itself, so it is not looked up here.</summary>
    static bool IsNumericId(string name) => name.Length > 1 && name[0] == '#' && name.Skip(1).All(c => c >= '0' && c <= '9');

    /// <summary>True if the user account exists (or is a numeric "#id", which sudo resolves).</summary>
    public static bool UserExists(string user) => !string.IsNullOrEmpty(user) && (IsNumericId(user) || getpwnam(user) != IntPtr.Zero);

    /// <summary>True if the group exists (or is a numeric "#id", which sudo resolves).</summary>
    public static bool GroupExists(string group) => !string.IsNullOrEmpty(group) && (IsNumericId(group) || getgrnam(group) != IntPtr.Zero);

    /// <summary>
    /// True if the user belongs to the group, either as its primary group or as a listed member.
    /// False if the user or group does not exist.
    /// </summary>
    public static bool IsUserInGroup(string user, string group)
    {
        var pwdPtr = getpwnam(user);
        if (pwdPtr == IntPtr.Zero) return false;
        // Read the passwd entry first: getpwnam and getgrnam return pointers to static buffers.
        var primaryGid = Marshal.PtrToStructure<Passwd>(pwdPtr).pw_gid;

        var grpPtr = getgrnam(group);
        if (grpPtr == IntPtr.Zero) return false;
        var grp = Marshal.PtrToStructure<Group>(grpPtr);
        if (grp.gr_gid == primaryGid) return true;

        // gr_mem is a NULL-terminated array of char* holding the names of the supplementary members.
        if (grp.gr_mem == IntPtr.Zero) return false;
        for (int i = 0; ; i++)
        {
            var namePtr = Marshal.ReadIntPtr(grp.gr_mem, i * IntPtr.Size);
            if (namePtr == IntPtr.Zero) return false;
            if (Marshal.PtrToStringAnsi(namePtr) == user) return true;
        }
    }

    public const int SIGINT = 2;

    [DllImport("libc", SetLastError = true)]
    public static extern int kill(int pid, int sig);


    [DllImport("libc")]
    public static extern uint getuid();

    public static bool IsRoot => getuid() == 0;

    public static void SetFilePermissions(string path, UnixFileMode mode, bool resetChildPermissions = false)
    {
        if (!resetChildPermissions)
        {
            FileSystemInfo info;
            if (File.Exists(path)) info = new FileInfo(path);
            else if (Directory.Exists(path)) info = new DirectoryInfo(path);
            else throw new FileNotFoundException(path);

            var prop = info.GetType().GetProperty("UnixFileMode", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            prop?.SetValue(info, mode);
            info.Refresh();
        }
        else
        {
            SetFilePermissions(path, mode, false);

            foreach (var e in new DirectoryInfo(path).GetFileSystemInfos())
            {
                var prop = e.GetType().GetProperty("UnixFileMode", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                prop?.SetValue(e, mode);
                e.Refresh();
            }
        }
    }

    public static UnixFileMode GetFilePermissions(string path)
    {
        FileSystemInfo info;
        if (File.Exists(path)) info = new FileInfo(path);
        else if (Directory.Exists(path)) info = new DirectoryInfo(path);
        else throw new FileNotFoundException(path);

        var prop = info.GetType().GetProperty("UnixFileMode", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        return (UnixFileMode)(prop?.GetValue(info) ?? throw new PlatformNotSupportedException("FileSystemInfo.UnixFileMode is not available."));
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int chown(string path, uint owner, uint group);

    public static void SetOwnerAndGroup(
        string path,
        string? username,
        string? groupName)
    {
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(groupName))
        {
            var current = GetOwnerAndGroup(path);
            if (string.IsNullOrEmpty(username)) username = current.Owner;
            if (string.IsNullOrEmpty(groupName)) groupName = current.Group;
        }

        IntPtr pwdPtr = getpwnam(username!);

        if (pwdPtr == IntPtr.Zero)
            throw new Exception($"User not found: {username}");

        Passwd pwd = Marshal.PtrToStructure<Passwd>(pwdPtr);

        IntPtr grpPtr = getgrnam(groupName!);

        if (grpPtr == IntPtr.Zero)
            throw new Exception($"Group not found: {groupName}");

        Group grp = Marshal.PtrToStructure<Group>(grpPtr);

        if (chown(path, pwd.pw_uid, grp.gr_gid) != 0)
        {
            int err = Marshal.GetLastWin32Error();
            throw new Exception($"chown failed. errno={err}");
        }
    }

    [DllImport("libc")]
    static extern IntPtr getpwuid(uint uid);

    [DllImport("libc")]
    static extern IntPtr getgrgid(uint gid);

    /// <summary>
    /// The owner and group of a file or directory, by name. The ids come from the stat record of the operating system, whose layout
    /// differs between macOS and Linux (and between Linux architectures), see <see cref="UnixMac"/> and <see cref="UnixLinux"/>.
    /// </summary>
    public static (string Owner, string Group) GetOwnerAndGroup(string path)
    {
        // The stat record also holds the mode of the file. The runtime reports it independently, which lets the readers check that the
        // record has the layout they assume, instead of trusting an owner that may have been read from the wrong place.
        var mode = (uint)GetFilePermissions(path);
        var (uid, gid) = OSInfo.IsMac ? UnixMac.GetIds(path, mode) : UnixLinux.GetIds(path, mode);

        string owner = uid.ToString();
        string group = gid.ToString();

        IntPtr pwdPtr = getpwuid(uid);
        if (pwdPtr != IntPtr.Zero)
        {
            var pwd = Marshal.PtrToStructure<Passwd>(pwdPtr);
            owner = Marshal.PtrToStringAnsi(pwd.pw_name)!;
        }

        IntPtr grpPtr = getgrgid(gid);
        if (grpPtr != IntPtr.Zero)
        {
            var grp = Marshal.PtrToStructure<Group>(grpPtr);
            group = Marshal.PtrToStringAnsi(grp.gr_name)!;
        }

        return (owner, group);
    }
}

/// <summary>
/// The stat record on macOS (64-bit inode variant: <c>struct stat</c> of Apple's sys/stat.h, with dev_t = int32, mode_t = uint16,
/// nlink_t = uint16, ino_t = uint64): st_dev@0, st_mode@4, st_nlink@6, st_ino@8, st_uid@16, st_gid@20. It is not declared as a managed
/// struct but read from a buffer at these offsets: the Linux layout puts the uid at 28, where macOS has padding, and a mistake in the size
/// of a declared struct would let the kernel write behind it.
/// </summary>
internal static class UnixMac
{
    const int BufferSize = 512; // the record is 144 bytes

    // On Intel Macs plain stat is the legacy variant with a 32-bit inode, which has a different layout; the one described above is
    // stat$INODE64. On Apple Silicon there is only the 64-bit variant, called stat.
    [DllImport("libc", EntryPoint = "stat$INODE64", SetLastError = true)]
    static extern int stat64(string path, byte[] buffer);

    [DllImport("libc", EntryPoint = "stat", SetLastError = true)]
    static extern int stat(string path, byte[] buffer);

    public static (uint Uid, uint Gid) GetIds(string path, uint expectedMode)
    {
        var buffer = new byte[BufferSize];
        var result = RuntimeInformation.ProcessArchitecture == Architecture.X64 ? stat64(path, buffer) : stat(path, buffer);
        if (result != 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return Parse(buffer, expectedMode);
    }

    /// <summary>Reads the ids from a stat record, after checking that its mode is where this layout says it is.</summary>
    internal static (uint Uid, uint Gid) Parse(byte[] stat, uint expectedMode)
    {
        var mode = BitConverter.ToUInt16(stat, 4);
        if ((mode & 0xFFFu) != (expectedMode & 0xFFFu))
        {
            throw new NotSupportedException($"The stat record of this macOS does not have the expected layout (mode {mode & 0xFFFu:x} at offset 4, expected {expectedMode & 0xFFFu:x}). The owner of the file cannot be determined.");
        }
        return (BitConverter.ToUInt32(stat, 16), BitConverter.ToUInt32(stat, 20));
    }
}

/// <summary>
/// The stat record on Linux. <c>statx</c> is preferred: its layout is the same on every architecture (glibc 2.28 and later, musl 1.2.5
/// and later): stx_mask@0, stx_uid@20, stx_gid@24, stx_mode@28. Without it (older musl, for example Alpine 3.19 and before) plain
/// <c>stat</c> is used, whose layout depends on the architecture: <see cref="UnixX64"/>, <see cref="UnixArm"/> or <see cref="UnixS390x"/>. Like on macOS the
/// records are read from a buffer, and the mode in the record is checked against the one the runtime reports.
/// </summary>
internal static class UnixLinux
{
    const int BufferSize = 512; // statx is 256 bytes, stat less
    const int AT_FDCWD = -100;
    const uint STATX_MODE = 0x2, STATX_UID = 0x8, STATX_GID = 0x10;

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    static extern int statx(int dirfd, string path, int flags, uint mask, byte[] buffer);

    [DllImport("libc", EntryPoint = "stat", SetLastError = true)]
    static extern int stat(string path, byte[] buffer);

    static bool statxAvailable = true;

    public static (uint Uid, uint Gid) GetIds(string path, uint expectedMode)
    {
        if (statxAvailable)
        {
            try
            {
                return GetIdsWithStatx(path, expectedMode);
            }
            catch (EntryPointNotFoundException)
            {
                statxAvailable = false; // this libc is too old for statx
            }
        }
        return GetIdsWithStat(path, expectedMode);
    }

    internal static (uint Uid, uint Gid) GetIdsWithStatx(string path, uint expectedMode)
    {
        var buffer = new byte[BufferSize];
        if (statx(AT_FDCWD, path, 0, STATX_MODE | STATX_UID | STATX_GID, buffer) != 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return ParseStatx(buffer, expectedMode);
    }

    internal static (uint Uid, uint Gid) GetIdsWithStat(string path, uint expectedMode)
    {
        var buffer = new byte[BufferSize];
        if (stat(path, buffer) != 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return ParseStat(buffer, RuntimeInformation.ProcessArchitecture, expectedMode);
    }

    internal static (uint Uid, uint Gid) ParseStatx(byte[] statx, uint expectedMode)
    {
        var mask = BitConverter.ToUInt32(statx, 0);
        if ((mask & (STATX_MODE | STATX_UID | STATX_GID)) != (STATX_MODE | STATX_UID | STATX_GID))
        {
            throw new NotSupportedException($"This file system did not report the owner of the file (statx mask {mask:x}).");
        }
        CheckMode(BitConverter.ToUInt16(statx, 28), expectedMode);
        return (BitConverter.ToUInt32(statx, 20), BitConverter.ToUInt32(statx, 24));
    }

    /// <summary>
    /// Architecture.S390x. The Architecture enum of netstandard2.0, in which the Api project compiles this file, has no such member,
    /// so its value (5, in the enum of .NET 5 and later) is used; a test makes sure that it is the same.
    /// </summary>
    internal const Architecture S390x = (Architecture)5;

    internal static (uint Uid, uint Gid) ParseStat(byte[] stat, Architecture architecture, uint expectedMode) => architecture switch
    {
        Architecture.X64 => UnixX64.Parse(stat, expectedMode),
        Architecture.Arm64 or Architecture.Arm => UnixArm.Parse(stat, expectedMode),
        S390x => UnixS390x.Parse(stat, expectedMode),
        _ => throw new NotSupportedException($"The layout of the stat record on {architecture} is not known, so the owner of a file cannot be determined."),
    };

    /// <summary>Throws if the mode that the stat record holds is not the one the runtime reported: the record has a different layout.</summary>
    internal static void CheckMode(uint mode, uint expectedMode)
    {
        if ((mode & 0xFFFu) != (expectedMode & 0xFFFu))
        {
            throw new NotSupportedException($"The stat record does not have the expected layout (mode {mode & 0xFFFu:x}, expected {expectedMode & 0xFFFu:x}). The owner of the file cannot be determined.");
        }
    }
}

/// <summary>
/// The <c>struct stat</c> of Linux on x86_64 (arch/x86/include/uapi/asm/stat.h): st_dev@0, st_ino@8, st_nlink@16 (8 bytes each),
/// then st_mode@24, st_uid@28, st_gid@32 (4 bytes each). Used when <c>statx</c> is not available, see <see cref="UnixLinux"/>.
/// </summary>
internal static class UnixX64
{
    public static (uint Uid, uint Gid) Parse(byte[] stat, uint expectedMode)
    {
        UnixLinux.CheckMode(BitConverter.ToUInt32(stat, 24), expectedMode);
        return (BitConverter.ToUInt32(stat, 28), BitConverter.ToUInt32(stat, 32));
    }
}

/// <summary>
/// The <c>struct stat</c> of Linux on s390x (arch/s390/include/uapi/asm/stat.h): st_dev@0, st_ino@8, st_nlink@16 (8 bytes each), then
/// st_mode@24, st_uid@28, st_gid@32 (4 bytes each), the same offsets as on x86_64. s390x is big-endian, which does not matter here:
/// the kernel fills the record in the byte order of the machine, and <see cref="BitConverter"/> reads it in the same order. Used when
/// <c>statx</c> is not available, see <see cref="UnixLinux"/>. This is taken from the kernel header and has not been run on s390x
/// hardware; the check of the mode in <see cref="UnixLinux.CheckMode"/> makes a mistake fail instead of misreading.
/// </summary>
internal static class UnixS390x
{
    public static (uint Uid, uint Gid) Parse(byte[] stat, uint expectedMode)
    {
        UnixLinux.CheckMode(BitConverter.ToUInt32(stat, 24), expectedMode);
        return (BitConverter.ToUInt32(stat, 28), BitConverter.ToUInt32(stat, 32));
    }
}

/// <summary>
/// The <c>struct stat</c> of Linux on arm64 (include/uapi/asm-generic/stat.h, which a few more architectures use): st_dev@0 and
/// st_ino@8 (8 bytes each), then st_mode@16, st_nlink@20, st_uid@24, st_gid@28 (4 bytes each). The mode comes before the link count
/// here, and the link count is 4 bytes wide, so the uid is at 24 and not at 28 as on x86_64. Used when <c>statx</c> is not
/// available, see <see cref="UnixLinux"/>.
/// <para>
/// 32-bit ARM has the same offsets for the fields that are read: its <c>struct stat64</c> (arch/arm/include/uapi/asm/stat.h, which
/// glibc and musl use for <c>stat</c>) has st_dev@0 (8 bytes), padding@8, st_ino@12 (4 bytes), and then again st_mode@16, st_nlink@20,
/// st_uid@24, st_gid@28. This is taken from the kernel header and has not been run on 32-bit hardware; the check of the mode in
/// <see cref="UnixLinux.CheckMode"/> makes a mistake fail instead of misreading. Normally <c>statx</c> is used there anyway.
/// </para>
/// </summary>
internal static class UnixArm
{
    public static (uint Uid, uint Gid) Parse(byte[] stat, uint expectedMode)
    {
        UnixLinux.CheckMode(BitConverter.ToUInt32(stat, 16), expectedMode);
        return (BitConverter.ToUInt32(stat, 24), BitConverter.ToUInt32(stat, 28));
    }
}
