using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace AspNetCoreSharedServer;

/// <summary>
/// The Windows counterpart of the owner and mode checks of the Unix side: an app's json file may only be changed by the user that the
/// app runs as, and by administrators. On Unix that is "owned by the user or root, and not writable by group or other"; on Windows it is
/// read from the access control list of the file.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsFileSecurity
{
    /// <summary>Every right that lets an account change the file, its permissions or its owner, or delete it.</summary>
    const FileSystemRights ChangeRights = FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteExtendedAttributes |
        FileSystemRights.WriteAttributes | FileSystemRights.Delete | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership |
        FileSystemRights.DeleteSubdirectoriesAndFiles;

    static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    static readonly SecurityIdentifier CreatorOwner = new(WellKnownSidType.CreatorOwnerSid, null);
    static readonly SecurityIdentifier TrustedInstaller = new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");

    /// <summary>The accounts that may always change a file of the server: SYSTEM, the administrators and Windows itself.</summary>
    static bool IsTrusted(SecurityIdentifier sid) =>
        sid == LocalSystem || sid == Administrators || sid == TrustedInstaller || sid == CreatorOwner;

    /// <summary>The SID of a local user, or null if there is no such user (or no user is given).</summary>
    internal static SecurityIdentifier? UserSid(string? user)
    {
        var name = user == null ? null : WindowsAccounts.LocalName(user);
        if (name == null) return null;
        try { return new NTAccount(Environment.MachineName, name).Translate(typeof(SecurityIdentifier)) as SecurityIdentifier; }
        catch (IdentityNotMappedException) { return null; }
    }

    /// <summary>
    /// Null if the file may only be changed by <paramref name="user"/> and the trusted accounts, otherwise what is wrong. Without a user
    /// only the trusted accounts may change it. A deny rule is not counted as protection: the allow rules decide.
    /// </summary>
    public static string? Check(string path, string? user)
    {
        FileSecurity security;
        try { security = new FileInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return $"its permissions cannot be read ({ex.Message})";
        }

        var allowed = UserSid(user);
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner)
        {
            return "it has no owner";
        }
        if (!IsTrusted(owner) && owner != allowed)
        {
            return $"it is owned by {Describe(owner)}";
        }

        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow || (rule.FileSystemRights & ChangeRights) == 0) continue;
            var sid = (SecurityIdentifier)rule.IdentityReference;
            if (IsTrusted(sid) || sid == allowed) continue;
            return $"{Describe(sid)} may change it";
        }
        return null;
    }

    /// <summary>True if the file is owned by an administrator or SYSTEM, the counterpart of "owned by root".</summary>
    public static bool IsOwnedByAdministrators(string path)
    {
        try
        {
            var owner = new FileInfo(path).GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            return owner != null && (owner == LocalSystem || owner == Administrators || owner == TrustedInstaller);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { return false; }
    }

    /// <summary>
    /// Sets the permissions of the file to what <see cref="Check"/> accepts, the counterpart of owner = user and mode 600: inheritance is
    /// switched off, SYSTEM and the administrators get full control, and <paramref name="user"/> gets read and write. Needs the right
    /// to change the permissions of the file, as the owner or as an administrator.
    /// </summary>
    public static void Protect(string path, string? user)
    {
        FileSecurity Build(bool withOwner)
        {
            var security = new FileSecurity();
            security.SetAccessRuleProtection(true, false); // no rules from the folder, and none of them kept
            security.AddAccessRule(new FileSystemAccessRule(LocalSystem, FileSystemRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(Administrators, FileSystemRights.FullControl, AccessControlType.Allow));
            if (UserSid(user) is { } sid)
            {
                security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.Modify, AccessControlType.Allow));
            }
            // The owner of a file can always change its permissions, so the file is owned by the administrators, like root owns a file on
            // Unix. An account that may not hand out that ownership leaves the owner as it is.
            if (withOwner) security.SetOwner(Administrators);
            return security;
        }

        var info = new FileInfo(path);
        try { info.SetAccessControl(Build(true)); }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.Security.AccessControl.PrivilegeNotHeldException)
        {
            info.SetAccessControl(Build(false));
        }
    }

    /// <summary>
    /// Sets the permissions of a folder the way the Unix side sets owner root and mode 700 (or 770 for the admin group): inheritance is
    /// switched off, SYSTEM and the administrators get full control over it and what it will contain, and <paramref name="adminGroup"/>,
    /// the local group of the people who may create applications, gets modify rights if it exists.
    /// </summary>
    public static void ProtectDirectory(string path, string? adminGroup)
    {
        DirectorySecurity Build(bool withOwner)
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            security.AddAccessRule(new FileSystemAccessRule(LocalSystem, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(Administrators, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            if (adminGroup != null && GroupSid(adminGroup) is { } group)
            {
                security.AddAccessRule(new FileSystemAccessRule(group, FileSystemRights.Modify, inherit, PropagationFlags.None, AccessControlType.Allow));
            }
            if (withOwner) security.SetOwner(Administrators); // see Protect
            return security;
        }

        var info = new DirectoryInfo(path);
        try { info.SetAccessControl(Build(true)); }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.Security.AccessControl.PrivilegeNotHeldException)
        {
            info.SetAccessControl(Build(false));
        }
    }

    /// <summary>
    /// True if this process runs as SYSTEM or as an administrator (elevated): the counterpart of <c>Unix.IsRoot</c>. Only then does the
    /// server change who owns or may change its folders and files; anybody else would lock themselves out.
    /// </summary>
    public static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User == LocalSystem || new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static bool IsLocalSystem()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User == LocalSystem;
    }

    /// <summary>The SID of a local group, or null if there is no such group.</summary>
    static SecurityIdentifier? GroupSid(string group)
    {
        var name = WindowsAccounts.LocalName(group);
        if (name == null) return null;
        try { return new NTAccount(Environment.MachineName, name).Translate(typeof(SecurityIdentifier)) as SecurityIdentifier; }
        catch (IdentityNotMappedException) { return null; }
    }

    static string Describe(SecurityIdentifier sid)
    {
        try { return ((NTAccount)sid.Translate(typeof(NTAccount))).Value; }
        catch (IdentityNotMappedException) { return sid.Value; }
    }
}
