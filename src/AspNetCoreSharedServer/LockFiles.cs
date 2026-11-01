using AspNetCoreSharedServer.Util;
using System.Runtime.Versioning;

namespace AspNetCoreSharedServer;

/// <summary>
/// The files of the machine wide locks (the lock of the configuration, and the lock that lets only one server run). They are inside the
/// configuration folder, not in the shared folder that <c>AsyncMutexLock</c> would use (<c>/usr/share/EstrellasDeEsperanza-AsyncMutexLock</c>,
/// <c>%ProgramData%\EstrellasDeEsperanza-AsyncMutexLock</c>), and that is a matter of security as much as of convenience: a lock is held
/// by whoever can open its file, so a lock file that every user can open is a lock that every user can hold, and anybody could stop the
/// server from starting, or from saving its configuration, by holding it. The configuration folder belongs to root (or SYSTEM and the
/// administrators) and to the group <see cref="Configuration.AdminGroup"/> (see <see cref="Configuration.EnsureConfigDirectory"/>), so
/// exactly the people who may use the server's configuration can use its locks, and nobody else can even see the files.
/// <para>
/// A server that runs with <c>--config-dir</c> has its locks in that folder, so it does not compete with the real one.
/// </para>
/// </summary>
internal static class LockFiles
{
    static readonly object gate = new object();
    static string? ensured;

    /// <summary>
    /// The path of the lock file <paramref name="name"/> in the configuration folder, ready to be used as the name of an
    /// <c>AsyncMutexLock</c> (a full path is used as it is). The folder and the file are created if they are not there. On Unix the file
    /// is for the owner and the group <see cref="Configuration.AdminGroup"/> (mode 660), so that a member of the group can lock what root
    /// created, and the other way round. Throws <see cref="UnauthorizedAccessException"/>, with what to do about it, if the user may not
    /// use the folder: waiting for the lock would never help.
    /// </summary>
    public static string PathFor(string name)
    {
        var directory = Configuration.ConfigDirectory;
        var path = Path.Combine(directory, name + ".lock");
        lock (gate)
        {
            if (ensured == path && File.Exists(path)) return path;
            try
            {
                Configuration.EnsureConfigDirectory(directory);
                if (!OSInfo.IsWindows) EnsureUnixFile(path);

                // Opening it is what the lock does later; if that is not allowed, say so now, in words, and do not let the lock wait
                // for "somebody else" to let go.
                using (new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite)) { }
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new UnauthorizedAccessException(
                    $"No permission to use the lock file '{path}'. Run aspnet-server as {(OSInfo.IsWindows ? "an administrator" : "root")}, " +
                    $"or as a member of the group {Configuration.AdminGroup}.", ex);
            }
            ensured = path;
            return path;
        }
    }

    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    [SupportedOSPlatform("freebsd")]
    static void EnsureUnixFile(string path)
    {
        const UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite;
        if (!Unix.CanOpenWithoutFollowing)
        {
            // The open flags of this platform are not known: create it, and set the mode by its path.
            using (new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite)) { }
            try { Unix.SetFilePermissions(path, mode); } catch (Exception) { }
            return;
        }

        // Created here (O_EXCL, which also refuses a symbolic link), or opened with O_NOFOLLOW: what is changed below is the file that was
        // opened, never something that a link in the folder points to.
        FileStream? stream;
        try
        {
            stream = Unix.OpenNoFollow(path, true, out _);
        }
        catch (IOException)
        {
            stream = Unix.OpenNoFollow(path, false, out var isLink);
            if (stream == null && isLink)
            {
                File.Delete(path); // only the link goes
                stream = Unix.OpenNoFollow(path, true, out _);
            }
        }
        using (stream)
        {
            if (stream == null) return;
            // The mode and the group are made right every time, so a file that an older version created for root alone (mode 644) is
            // repaired. Only the owner or root can do that; for anybody else it is not needed, or not possible, and the check
            // in <see cref="PathFor"/> tells.
            try { Unix.SetFilePermissions(stream.SafeFileHandle, mode); } catch (Exception) { }
            try
            {
                if (Unix.GroupExists(Configuration.AdminGroup)) Unix.SetOwnerAndGroup(stream.SafeFileHandle, null, Configuration.AdminGroup);
            }
            catch (Exception) { }
        }
    }
}
