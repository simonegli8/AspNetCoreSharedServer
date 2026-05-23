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

    public static void GrantUnixPermissions(string path, UnixFileMode mode, bool resetChildPermissions = false)
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
            GrantUnixPermissions(path, mode, false);

            foreach (var e in new DirectoryInfo(path).GetFileSystemInfos())
            {
                var prop = e.GetType().GetProperty("UnixFileMode", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                prop.SetValue(e, mode);
                e.Refresh();
            }
        }
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
}
