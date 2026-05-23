using Mono.Cecil.Cil;
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
    All = 0x8ff
}

public class Unix
{
    [DllImport("libc", SetLastError = true)]
    public static extern int chmod(string path, uint mode);

    [DllImport("libc", SetLastError = true)]
    public static extern int seteuid(uint euid); 
    
    [DllImport("libc", SetLastError = true)]
    public static extern int setegid(uint egid);

    [StructLayout(LayoutKind.Sequential)]
    public struct Passwd
    {
        public IntPtr pw_name;
        public IntPtr pw_passwd;
        public uint pw_uid;
        public uint pw_gid;
        public IntPtr pw_gecos;
        public IntPtr pw_dir;
        public IntPtr pw_shell;
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

    public const int SIGINT = 2;

    [DllImport("libc", SetLastError = true)]
    public static extern int kill(int pid, int sig);


    [DllImport("libc")]
    public static extern uint getuid();

    public static void SetFilePermissions(string path, UnixFileMode mode, bool resetChildPermissions = false)
    {
        if (!resetChildPermissions)
        {
            FileSystemInfo info;
            if (File.Exists(path)) info = new FileInfo(path);
            else if (Directory.Exists(path)) info = new DirectoryInfo(path);
            else throw new FileNotFoundException(path);

            var prop = info.GetType().GetProperty("UnixFileMode", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            prop.SetValue(info, mode);
            info.Refresh();
        }
        else
        {
            SetFilePermissions(path, mode, false);

            foreach (var e in new DirectoryInfo(path).GetFileSystemInfos())
            {
                var prop = e.GetType().GetProperty("UnixFileMode", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                prop.SetValue(e, mode);
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
        return (UnixFileMode)prop.GetValue(info)!;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int chown(string path, uint owner, uint group);

    public static void SetOwnerAndGroup(
        string path,
        string username,
        string groupName)
    {
        IntPtr pwdPtr = getpwnam(username);

        if (pwdPtr == IntPtr.Zero)
            throw new Exception($"User not found: {username}");

        Passwd pwd = Marshal.PtrToStructure<Passwd>(pwdPtr);

        IntPtr grpPtr = getgrnam(groupName);

        if (grpPtr == IntPtr.Zero)
            throw new Exception($"Group not found: {groupName}");

        Group grp = Marshal.PtrToStructure<Group>(grpPtr);

        if (chown(path, pwd.pw_uid, grp.gr_gid) != 0)
        {
            int err = Marshal.GetLastWin32Error();
            throw new Exception($"chown failed. errno={err}");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Stat
    {
        public ulong st_dev;
        public ulong st_ino;
        public ulong st_nlink;
        public uint st_mode;
        public uint st_uid;
        public uint st_gid;
        public uint __pad0;
        public ulong st_rdev;
        public long st_size;
        public long st_blksize;
        public long st_blocks;
        public long st_atime;
        public ulong st_atime_nsec;
        public long st_mtime;
        public ulong st_mtime_nsec;
        public long st_ctime;
        public ulong st_ctime_nsec;
        public long __unused0;
        public long __unused1;
        public long __unused2;
    }

    [DllImport("libc", SetLastError = true)]
    static extern int stat(string path, out Stat buf);

    [DllImport("libc")]
    static extern IntPtr getpwuid(uint uid);

    [DllImport("libc")]
    static extern IntPtr getgrgid(uint gid);

    public static (string Owner, string Group) GetOwnerAndGroup(string path)
    {
        if (stat(path, out var statBuf) != 0)
        {
            throw new System.ComponentModel.Win32Exception(
                Marshal.GetLastWin32Error());
        }

        string owner = statBuf.st_uid.ToString();
        string group = statBuf.st_gid.ToString();

        IntPtr pwdPtr = getpwuid(statBuf.st_uid);
        if (pwdPtr != IntPtr.Zero)
        {
            var pwd = Marshal.PtrToStructure<Passwd>(pwdPtr);
            owner = Marshal.PtrToStringAnsi(pwd.pw_name)!;
        }

        IntPtr grpPtr = getgrgid(statBuf.st_gid);
        if (grpPtr != IntPtr.Zero)
        {
            var grp = Marshal.PtrToStructure<Group>(grpPtr);
            group = Marshal.PtrToStringAnsi(grp.gr_name)!;
        }

        return (owner, group);
    }
}
