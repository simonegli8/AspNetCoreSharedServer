using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Xunit;

namespace AspNetCoreSharedServer.Tests;

/// <summary>
/// The json file of an app may only be changed by the user that the app runs as, and by administrators: the Windows counterpart of the
/// owner and mode checks on Unix. The tests use files in a temporary folder and the account that runs them, and need no administrator
/// rights. They are skipped elsewhere.
/// </summary>
[SupportedOSPlatform("windows")]
public class WindowsFileSecurityTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "aspsrv-acl-" + Guid.NewGuid().ToString("N"));

    public WindowsFileSecurityTests()
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(folder);
    }

    public void Dispose()
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
    }

    static string Me => Environment.UserName;

    static void RequireWindows() => Skip.IfNot(OperatingSystem.IsWindows(), "Windows ACLs.");

    string NewFile()
    {
        var file = Path.Combine(folder, "app.json");
        File.WriteAllText(file, "{}");
        return file;
    }

    /// <summary>A file with exactly these rules, and none inherited: the starting point of a test.</summary>
    static void SetRules(string file, params FileSystemAccessRule[] rules)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(true, false);
        foreach (var rule in rules) security.AddAccessRule(rule);
        new FileInfo(file).SetAccessControl(security);
    }

    static FileSystemAccessRule Allow(WellKnownSidType sid, FileSystemRights rights) =>
        new(new SecurityIdentifier(sid, null), rights, AccessControlType.Allow);

    static FileSystemAccessRule AllowMe(FileSystemRights rights) =>
        new(WindowsIdentity.GetCurrent().User!, rights, AccessControlType.Allow);

    [SkippableFact]
    public void A_file_that_only_the_user_and_the_administrators_can_change_is_secure()
    {
        RequireWindows();
        var file = NewFile();
        SetRules(file, AllowMe(FileSystemRights.Modify), Allow(WellKnownSidType.BuiltinAdministratorsSid, FileSystemRights.FullControl),
            Allow(WellKnownSidType.LocalSystemSid, FileSystemRights.FullControl));

        Assert.Null(WindowsFileSecurity.Check(file, Me));
    }

    [SkippableFact]
    public void The_same_file_is_not_secure_for_another_user_or_for_none()
    {
        RequireWindows();
        var file = NewFile();
        SetRules(file, AllowMe(FileSystemRights.Modify), Allow(WellKnownSidType.BuiltinAdministratorsSid, FileSystemRights.FullControl),
            Allow(WellKnownSidType.LocalSystemSid, FileSystemRights.FullControl));

        // The account that runs the test may change it, but it is neither the user of the app nor trusted.
        Assert.NotNull(WindowsFileSecurity.Check(file, null));
        Assert.NotNull(WindowsFileSecurity.Check(file, "no-such-user-xyz"));
    }

    [SkippableTheory]
    [InlineData(WellKnownSidType.BuiltinUsersSid)]
    [InlineData(WellKnownSidType.WorldSid)] // Everyone
    [InlineData(WellKnownSidType.AuthenticatedUserSid)]
    public void A_file_that_a_group_can_change_is_not_secure(WellKnownSidType group)
    {
        RequireWindows();
        var file = NewFile();
        SetRules(file, AllowMe(FileSystemRights.Modify), Allow(WellKnownSidType.BuiltinAdministratorsSid, FileSystemRights.FullControl),
            Allow(WellKnownSidType.LocalSystemSid, FileSystemRights.FullControl), Allow(group, FileSystemRights.Modify));

        var problem = WindowsFileSecurity.Check(file, Me);

        Assert.NotNull(problem);
        Assert.Contains("may change it", problem);
    }

    [SkippableTheory]
    [InlineData(FileSystemRights.WriteData)]
    [InlineData(FileSystemRights.AppendData)]
    [InlineData(FileSystemRights.Delete)]
    [InlineData(FileSystemRights.ChangePermissions)]
    [InlineData(FileSystemRights.TakeOwnership)]
    public void Any_right_to_change_the_file_counts_not_only_full_control(FileSystemRights right)
    {
        RequireWindows();
        var file = NewFile();
        SetRules(file, AllowMe(FileSystemRights.Modify), Allow(WellKnownSidType.BuiltinAdministratorsSid, FileSystemRights.FullControl),
            Allow(WellKnownSidType.LocalSystemSid, FileSystemRights.FullControl), Allow(WellKnownSidType.BuiltinUsersSid, right));

        Assert.NotNull(WindowsFileSecurity.Check(file, Me));
    }

    [SkippableFact]
    public void A_group_that_can_only_read_the_file_is_fine()
    {
        RequireWindows();
        var file = NewFile();
        SetRules(file, AllowMe(FileSystemRights.Modify), Allow(WellKnownSidType.BuiltinAdministratorsSid, FileSystemRights.FullControl),
            Allow(WellKnownSidType.LocalSystemSid, FileSystemRights.FullControl),
            Allow(WellKnownSidType.BuiltinUsersSid, FileSystemRights.ReadAndExecute));

        Assert.Null(WindowsFileSecurity.Check(file, Me));
    }

    [SkippableFact]
    public void Protect_leaves_exactly_the_user_and_the_administrators_and_switches_inheritance_off()
    {
        RequireWindows();
        var file = NewFile();
        // Start from something wide open, as a file in a folder where everybody can write.
        SetRules(file, Allow(WellKnownSidType.WorldSid, FileSystemRights.FullControl));
        Assert.NotNull(WindowsFileSecurity.Check(file, Me));

        WindowsFileSecurity.Protect(file, Me);

        Assert.Null(WindowsFileSecurity.Check(file, Me));
        var security = new FileInfo(file).GetAccessControl(AccessControlSections.Access);
        Assert.True(security.AreAccessRulesProtected);
        var sids = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
            .Select(rule => (SecurityIdentifier)rule.IdentityReference).ToHashSet();
        Assert.Equal(new HashSet<SecurityIdentifier>
        {
            new(WellKnownSidType.LocalSystemSid, null), new(WellKnownSidType.BuiltinAdministratorsSid, null), WindowsIdentity.GetCurrent().User!,
        }, sids);
    }

    [SkippableFact]
    public void Protect_for_a_user_that_does_not_exist_leaves_only_the_administrators()
    {
        RequireWindows();
        var file = NewFile();

        WindowsFileSecurity.Protect(file, "no-such-user-xyz");

        var security = new FileInfo(file).GetAccessControl(AccessControlSections.Access);
        var sids = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
            .Select(rule => (SecurityIdentifier)rule.IdentityReference).ToHashSet();
        Assert.Equal(new HashSet<SecurityIdentifier> { new(WellKnownSidType.LocalSystemSid, null), new(WellKnownSidType.BuiltinAdministratorsSid, null) }, sids);
    }

    [SkippableFact]
    public void An_account_that_can_change_the_file_is_named_in_the_message()
    {
        RequireWindows();
        var file = NewFile();
        SetRules(file, AllowMe(FileSystemRights.Modify), Allow(WellKnownSidType.BuiltinAdministratorsSid, FileSystemRights.FullControl),
            Allow(WellKnownSidType.LocalSystemSid, FileSystemRights.FullControl), Allow(WellKnownSidType.WorldSid, FileSystemRights.Write));

        Assert.Contains("Everyone", WindowsFileSecurity.Check(file, Me));
    }
}
