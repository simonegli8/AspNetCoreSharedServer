using System.Security.AccessControl;
using System.Text;
using AspNetCoreSharedServer.Util;

namespace AspNetCoreSharedServer;

/// <summary>
/// A json file of the server (the configuration.json, or the json file of an app), read and written so that nobody who can write to its
/// folder can redirect the server to another file.
/// <para>
/// Reading: the owner and the permissions come from the handle of the open file, not from its path, and the text is read from the same
/// handle. Checking the path and then reading the path leaves a gap in which another file can be put in place of the one that was
/// checked. On Unix the file is opened with O_NOFOLLOW, so the open itself fails for a symbolic link. Windows also blocks writing,
/// renaming and deleting the file while it is open (<see cref="FileShare.Read"/> without <see cref="FileShare.Delete"/>).
/// </para>
/// <para>
/// Writing: see <see cref="Write"/>.
/// </para>
/// A symbolic link (on Windows any reparse point) is never trusted, see <see cref="IsLink"/>.
/// </summary>
internal sealed class SecureFile
{
    /// <summary>The content of the file. Empty for a link, which is not read.</summary>
    public string Text { get; }

    /// <summary>
    /// True if the path is a symbolic link. A link is rejected: the file it points to is not the one in the folder of the server, and
    /// writing the server's own state back would write through the link. On Unix this is decided by the open itself (O_NOFOLLOW), so
    /// there is nothing to race. Without it (Windows, or a Unix whose open flags are not known, see
    /// <see cref="Unix.CanOpenWithoutFollowing"/>) the path is looked at before and after it is opened, and a link that is swapped in and
    /// out again around the open cannot be told apart; the content that is read is then still that of a file whose owner and
    /// permissions were checked through the handle. Creating a link on Windows takes administrator rights.
    /// </summary>
    public bool IsLink { get; }

    readonly FileSecurity? windows;
    readonly string? owner;
    readonly string? group;
    readonly UnixFileMode mode;

    SecureFile(string text, bool isLink, FileSecurity? windows, string? owner, string? group, UnixFileMode mode)
    {
        Text = text;
        IsLink = isLink;
        this.windows = windows;
        this.owner = owner;
        this.group = group;
        this.mode = mode;
    }

    static SecureFile Link() => new SecureFile("", true, null, null, null, UnixFileMode.None);

    /// <summary>The owner of the file on Unix (null on Windows, and for a link).</summary>
    public string? Owner => owner;

    /// <summary>The group of the file on Unix (null on Windows, and for a link).</summary>
    public string? Group => group;

    /// <summary>True if the file is owned by root, on Windows by an administrator or SYSTEM.</summary>
    public bool OwnedByRoot => !IsLink && (OSInfo.IsWindows ? WindowsFileSecurity.IsOwnedByAdministrators(windows) : owner == "root");

    /// <summary>
    /// True if the file can be changed by someone who may not: a link; on Unix when its owner is neither root, nor
    /// <paramref name="user"/>, nor the user that this process runs as, or when group or other can write it; on Windows when its owner or
    /// its permissions allow somebody else than <paramref name="user"/>, the administrators and SYSTEM to change it.
    /// <para>
    /// The user that runs the process is trusted because it can change what the process does anyway. The user in a configuration is no
    /// help for that: it is not known yet when the configuration.json itself is read, so a server that does not run as root could
    /// otherwise never load a file of its own.
    /// </para>
    /// </summary>
    public bool IsInsecure(string? user)
    {
        if (IsLink) return true;
        if (OSInfo.IsWindows) return WindowsFileSecurity.Check(windows, user) != null;
        return owner != user && owner != "root" && owner != Environment.UserName ||
            mode.HasFlag(UnixFileMode.GroupWrite) ||
            mode.HasFlag(UnixFileMode.OtherWrite);
    }

    /// <summary>What is wrong with the file, as part of a message: a link, or on Windows the problem found in its permissions.</summary>
    public string? Problem(string? user) =>
        IsLink ? "it is a symbolic link" :
        OSInfo.IsWindows ? WindowsFileSecurity.Check(windows, user) : null;

    static bool PathIsLink(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; } // opening it reports what is wrong
    }

    /// <summary>
    /// Reads the file. Somebody else that has it open for writing (the Api in another process, on Windows) makes the open fail for a
    /// moment, so it is tried again a few times.
    /// </summary>
    public static SecureFile Read(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return ReadOnce(path); }
            catch (IOException) when (attempt < 4 && File.Exists(path)) { Thread.Sleep(50); }
        }
    }

    /// <summary>Like <see cref="Read"/>, but null if there is nothing at the path. A link, even a dangling one, is a link.</summary>
    public static SecureFile? ReadIfExists(string path)
    {
        try { return Read(path); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return null; }
    }

    static SecureFile ReadOnce(string path)
    {
        var atomic = !OSInfo.IsWindows && Unix.CanOpenWithoutFollowing;
        FileStream? opened;
        if (atomic) opened = Unix.OpenNoFollow(path, false, out _);
        else if (PathIsLink(path)) opened = null;
        else opened = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (opened == null) return Link();

        using var stream = opened;
        FileSecurity? windows = null;
        string? owner = null, group = null;
        var mode = UnixFileMode.None;
        if (OSInfo.IsWindows) windows = WindowsFileSecurity.Snapshot(stream);
        else
        {
            mode = Unix.GetFilePermissions(stream.SafeFileHandle);
            (owner, group) = Unix.GetOwnerAndGroup(stream.SafeFileHandle);
        }
        if (!atomic && PathIsLink(path)) return Link();

        using var reader = new StreamReader(stream, Encoding.UTF8, true);
        return new SecureFile(reader.ReadToEnd(), false, windows, owner, group, mode);
    }

    /// <summary>
    /// Writes a json file of the server so that the write cannot be redirected to another file by somebody who can write to the folder.
    /// <para>
    /// Unix: the content goes to a new file next to it, created with O_CREAT | O_EXCL | O_NOFOLLOW and mode 0600, whose owner is set
    /// through its handle (fchown) before anything is written; then it is renamed over <paramref name="path"/>. A rename replaces the
    /// directory entry and touches nothing it pointed to, so neither a symbolic link nor a hard link at the path (to a file of root,
    /// say) is written through, owned or changed, which writing to the existing path would do. Readers see the old or the new file, never
    /// half of one. The owner is <paramref name="user"/> and <paramref name="group"/> (the file stays the one of whoever it is
    /// given to), or the current user if there is none. Failing to set the owner is ignored unless
    /// <paramref name="requireOwner"/> is set, which a caller uses when the file must not become more trusted than it was: an
    /// insecure file is written back with the owner it has, so that it stays insecure.
    /// </para>
    /// <para>
    /// Windows: the file is written in place. It is the caller's task to set its permissions before. Windows needs administrator
    /// rights to create a link, and the caller has removed a link that was there.
    /// </para>
    /// </summary>
    /// <param name="current">What <see cref="ReadIfExists"/> found at the path before, or null.</param>
    public static void Write(string path, string text, string? user, string? group, SecureFile? current, bool requireOwner)
    {
        if (OSInfo.IsWindows)
        {
            File.WriteAllText(path, text);
            return;
        }

        // Writing to the old file in place needed write access to it; renaming over it does not, so the same rule is kept: only root,
        // or the owner of the file, replaces it (the Api runs as an ordinary member of the admin group).
        if (current != null && !Unix.IsRoot && current.Owner != Environment.UserName)
        {
            throw new UnauthorizedAccessException($"Access to the path '{path}' is denied.");
        }

        if (!Unix.CanOpenWithoutFollowing)
        {
            // The platform's open flags are not known: set the permissions first and write in place, as before.
            if (current == null) File.WriteAllText(path, "");
            try { if (!string.IsNullOrEmpty(user)) Unix.SetOwnerAndGroup(path, user, group); }
            catch (Exception) { if (requireOwner) return; }
            try { Unix.SetFilePermissions(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
            catch (Exception) { }
            File.WriteAllText(path, text);
            return;
        }

        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        var ownerFailed = false;
        try
        {
            using (var stream = Unix.OpenNoFollow(temp, true, out _)!)
            {
                if (!string.IsNullOrEmpty(user) || !string.IsNullOrEmpty(group))
                {
                    try { Unix.SetOwnerAndGroup(stream.SafeFileHandle, user, group); }
                    catch (Exception) { ownerFailed = true; }
                }
                if (ownerFailed && requireOwner) { } // see below: nothing is written
                else
                {
                    try { Unix.SetFilePermissions(stream.SafeFileHandle, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
                    catch (Exception) { }
                    var bytes = new UTF8Encoding(false).GetBytes(text);
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
            }
            // The owner that the file has to keep could not be given to the new file: leave the old file alone rather than let the
            // server's own file replace it, which would make it more trusted than it was.
            if (ownerFailed && requireOwner) File.Delete(temp);
            else Unix.Rename(temp, path);
        }
        catch (Exception)
        {
            try { File.Delete(temp); } catch (Exception) { }
            throw;
        }
    }
}
