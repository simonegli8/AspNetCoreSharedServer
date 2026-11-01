using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Xunit;
using Mode = System.IO.UnixFileMode;

namespace AspNetCoreSharedServer.Tests;

/// <summary>
/// <see cref="SecureFile"/> reads a json file of the server through one open handle: the owner and permissions that are checked are those
/// of the file that is read, and a symbolic link is never trusted. The tests use files in a temporary folder and need no administrator
/// rights, except that creating a symbolic link needs them on Windows (without developer mode); those tests are skipped without.
/// </summary>
public class SecureFileTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "aspsrv-secure-" + Guid.NewGuid().ToString("N"));

    public SecureFileTests() => Directory.CreateDirectory(folder);

    public void Dispose()
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
    }

    string NewFile(string text = "{ \"a\": 1 }", string name = "app.json")
    {
        var file = Path.Combine(folder, name);
        File.WriteAllText(file, text);
        return file;
    }

    string NewLink(string target)
    {
        var link = Path.Combine(folder, "link.json");
        try { File.CreateSymbolicLink(link, target); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { Skip.If(true, "Cannot create a symbolic link here: " + ex.Message); }
        return link;
    }

    static string Me => Environment.UserName;

    [Fact]
    public void The_text_of_the_file_is_read()
    {
        var file = NewFile("{ \"b\": 2 }");

        var secure = SecureFile.Read(file);

        Assert.Equal("{ \"b\": 2 }", secure.Text);
        Assert.False(secure.IsLink);
    }

    [Fact]
    public void A_byte_order_mark_is_not_part_of_the_text()
    {
        var file = Path.Combine(folder, "bom.json");
        File.WriteAllText(file, "{}", new System.Text.UTF8Encoding(true));

        Assert.Equal("{}", SecureFile.Read(file).Text);
    }

    [SkippableFact]
    public void A_symbolic_link_is_rejected_and_its_target_is_not_read()
    {
        var target = NewFile("{ \"secret\": true }", "target.json");
        var link = NewLink(target);

        var secure = SecureFile.Read(link);

        Assert.True(secure.IsLink);
        Assert.True(secure.IsInsecure(Me));
        Assert.True(secure.IsInsecure(null));
        Assert.False(secure.OwnedByRoot);
        Assert.Equal("", secure.Text);
        Assert.Contains("symbolic link", secure.Problem(Me));
    }

    [SkippableFact]
    public void A_link_to_a_folder_or_a_missing_file_is_still_a_link_and_not_an_error()
    {
        var link = NewLink(Path.Combine(folder, "missing.json"));

        Assert.True(SecureFile.Read(link).IsLink);
    }

    [Fact]
    public async Task A_file_that_is_briefly_open_for_writing_is_read_after_it_is_released()
    {
        var file = NewFile("{ \"c\": 3 }");
        using var writer = new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.None);
        var release = Task.Run(async () => { await Task.Delay(100); writer.Dispose(); });

        var secure = SecureFile.Read(file);
        await release;

        Assert.Equal("{ \"c\": 3 }", secure.Text);
    }

    [Fact]
    public void A_missing_file_is_reported_and_not_retried_forever()
    {
        Assert.ThrowsAny<IOException>(() => SecureFile.Read(Path.Combine(folder, "nothing.json")));
    }

    // ---- Unix: owner and mode from the handle -------------------------------------------------------------------------------------

    static void RequireUnix() => Skip.If(OperatingSystem.IsWindows(), "Unix owner and mode.");

    [SkippableFact]
    public void Unix_the_owner_from_the_handle_is_the_owner_from_the_path()
    {
        RequireUnix();
        var file = NewFile();

        var secure = SecureFile.Read(file);

        Assert.Equal(Unix.GetOwnerAndGroup(file).Owner, secure.Owner);
    }

    [SkippableFact]
    public void Unix_a_file_of_the_user_that_only_the_user_can_write_is_secure()
    {
        RequireUnix();
        var file = NewFile();
        File.SetUnixFileMode(file, Mode.UserRead | Mode.UserWrite);

        var secure = SecureFile.Read(file);

        Assert.False(secure.IsInsecure(Me));
        Assert.Null(secure.Problem(Me));
    }

    [SkippableFact]
    public void Unix_a_file_that_group_or_other_can_write_is_insecure()
    {
        RequireUnix();
        var file = NewFile();
        File.SetUnixFileMode(file, Mode.UserRead | Mode.UserWrite | Mode.GroupWrite);
        Assert.True(SecureFile.Read(file).IsInsecure(Me));

        File.SetUnixFileMode(file, Mode.UserRead | Mode.UserWrite | Mode.OtherWrite);
        Assert.True(SecureFile.Read(file).IsInsecure(Me));
    }

    [SkippableFact]
    public void Unix_a_file_of_the_user_that_runs_the_process_is_secure_whatever_user_is_named()
    {
        RequireUnix();
        var file = NewFile();
        File.SetUnixFileMode(file, Mode.UserRead | Mode.UserWrite);

        var secure = SecureFile.Read(file);

        // The user in a configuration is not known yet when its own file is read, so a server that is not root must be able to load
        // a file that it owns itself.
        Assert.False(secure.IsInsecure("no-such-user-xyz"));
        Assert.False(secure.IsInsecure(null));
    }

    [SkippableFact]
    public void Unix_a_file_of_somebody_else_than_root_the_named_user_and_the_current_user_is_insecure()
    {
        RequireUnix();
        Skip.IfNot(Unix.IsRoot, "Only root can give a file to another user.");
        Skip.IfNot(Unix.UserExists("nobody"), "No user nobody.");
        var file = NewFile();
        File.SetUnixFileMode(file, Mode.UserRead | Mode.UserWrite);
        Unix.SetOwnerAndGroup(file, "nobody", null);

        var secure = SecureFile.Read(file);

        Assert.True(secure.IsInsecure(null)); // root is running this, and the file is nobody's
        Assert.True(secure.IsInsecure("no-such-user-xyz"));
        Assert.False(secure.IsInsecure("nobody")); // unless that user is the one named
    }

    // ---- Unix: O_NOFOLLOW and the atomic write -------------------------------------------------------------------------------------

    string[] Leftovers() => Directory.GetFiles(folder, "*.tmp");

    void HardLink(string target, string link)
    {
        using var ln = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ln") { ArgumentList = { target, link }, UseShellExecute = false })!;
        ln.WaitForExit();
        Skip.If(ln.ExitCode != 0, "Cannot create a hard link here.");
    }

    [SkippableFact]
    public void Unix_the_open_flags_of_this_platform_are_known_and_behave()
    {
        RequireUnix();
        Skip.If(!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS(), "The open flags of this system are not known; the fallback is used.");

        // DetectOpenFlags tries the constants on a real file and a real symbolic link, so this fails if one is wrong for this architecture.
        Assert.True(Unix.CanOpenWithoutFollowing);
    }

    [SkippableFact]
    public void Unix_opening_a_symbolic_link_without_following_it_reports_a_link_and_reads_nothing()
    {
        RequireUnix();
        Skip.IfNot(Unix.CanOpenWithoutFollowing, "O_NOFOLLOW is not available.");
        var link = NewLink(NewFile("{ \"secret\": true }", "target.json"));

        Assert.Null(Unix.OpenNoFollow(link, false, out var isLink));
        Assert.True(isLink);
        using var file = Unix.OpenNoFollow(NewFile(), false, out isLink);
        Assert.NotNull(file);
        Assert.False(isLink);
    }

    [SkippableFact]
    public void Unix_creating_a_new_file_refuses_a_link_and_an_existing_file()
    {
        RequireUnix();
        Skip.IfNot(Unix.CanOpenWithoutFollowing, "O_NOFOLLOW is not available.");
        var target = NewFile("{ \"secret\": true }", "target.json");
        var link = NewLink(target);

        Assert.ThrowsAny<IOException>(() => Unix.OpenNoFollow(link, true, out _));
        Assert.ThrowsAny<IOException>(() => Unix.OpenNoFollow(target, true, out _));
        Assert.Equal("{ \"secret\": true }", File.ReadAllText(target));
    }

    [SkippableFact]
    public void Unix_write_creates_a_new_file_with_mode_600_and_leaves_nothing_behind()
    {
        RequireUnix();
        var path = Path.Combine(folder, "new.json");

        SecureFile.Write(path, "{ \"n\": 1 }", null, null, null, false);

        Assert.Equal("{ \"n\": 1 }", File.ReadAllText(path));
        Assert.Equal(Mode.UserRead | Mode.UserWrite, File.GetUnixFileMode(path));
        Assert.Equal(Me, Unix.GetOwnerAndGroup(path).Owner);
        Assert.Empty(Leftovers());
    }

    [SkippableFact]
    public void Unix_write_replaces_an_existing_file_in_one_step()
    {
        RequireUnix();
        var path = NewFile("{ \"old\": true }");
        File.SetUnixFileMode(path, Mode.UserRead | Mode.UserWrite);

        SecureFile.Write(path, "{ \"new\": true }", null, null, SecureFile.Read(path), false);

        Assert.Equal("{ \"new\": true }", File.ReadAllText(path));
        Assert.Equal(Mode.UserRead | Mode.UserWrite, File.GetUnixFileMode(path));
        Assert.Empty(Leftovers());
    }

    [SkippableFact]
    public void Unix_write_replaces_a_symbolic_link_and_leaves_its_target_alone()
    {
        RequireUnix();
        var target = NewFile("{ \"secret\": true }", "target.json");
        var link = NewLink(target);

        SecureFile.Write(link, "{ \"new\": true }", null, null, null, false);

        Assert.Equal("{ \"secret\": true }", File.ReadAllText(target));
        Assert.Equal("{ \"new\": true }", File.ReadAllText(link));
        Assert.False(SecureFile.Read(link).IsLink); // the link is gone, a file of its own is there
    }

    [SkippableFact]
    public void Unix_write_does_not_write_through_a_hard_link()
    {
        RequireUnix();
        var target = NewFile("{ \"secret\": true }", "target.json");
        var link = Path.Combine(folder, "hard.json");
        HardLink(target, link);

        SecureFile.Write(link, "{ \"new\": true }", null, null, null, false);

        Assert.Equal("{ \"secret\": true }", File.ReadAllText(target)); // the other name of the same file is not touched
        Assert.Equal("{ \"new\": true }", File.ReadAllText(link));
    }

    [SkippableFact]
    public void Unix_write_gives_the_new_file_to_the_owner_it_is_told_to_when_run_as_root()
    {
        RequireUnix();
        Skip.IfNot(Unix.IsRoot, "Only root can give a file to somebody else.");
        Skip.IfNot(Unix.UserExists("nobody"), "No user nobody.");
        var path = Path.Combine(folder, "owned.json");

        SecureFile.Write(path, "{}", "nobody", null, null, false);

        Assert.Equal("nobody", Unix.GetOwnerAndGroup(path).Owner);
        Assert.Equal(Mode.UserRead | Mode.UserWrite, File.GetUnixFileMode(path));
    }

    [SkippableFact]
    public void Unix_write_leaves_the_old_file_alone_when_the_owner_it_must_keep_cannot_be_set()
    {
        RequireUnix();
        var path = NewFile("{ \"old\": true }");

        // requireOwner: the file may not become more trusted than it was, so if the owner cannot be given, nothing is replaced
        SecureFile.Write(path, "{ \"new\": true }", "no-such-user-xyz", null, SecureFile.Read(path), true);

        Assert.Equal("{ \"old\": true }", File.ReadAllText(path));
        Assert.Empty(Leftovers());
    }

    [SkippableFact]
    public void Unix_write_without_the_required_owner_still_writes_when_it_is_only_asked_for()
    {
        RequireUnix();
        var path = NewFile("{ \"old\": true }");

        SecureFile.Write(path, "{ \"new\": true }", "no-such-user-xyz", null, SecureFile.Read(path), false);

        Assert.Equal("{ \"new\": true }", File.ReadAllText(path));
    }

    // ---- Windows: permissions from the handle --------------------------------------------------------------------------------------

    static void RequireWindows() => Skip.IfNot(OperatingSystem.IsWindows(), "Windows ACLs.");

    [SupportedOSPlatform("windows")]
    static void SetRules(string file, params FileSystemAccessRule[] rules)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(true, false);
        foreach (var rule in rules) security.AddAccessRule(rule);
        new FileInfo(file).SetAccessControl(security);
    }

    [SupportedOSPlatform("windows")]
    static FileSystemAccessRule Allow(WellKnownSidType sid, FileSystemRights rights) =>
        new(new SecurityIdentifier(sid, null), rights, AccessControlType.Allow);

    [SkippableFact]
    [SupportedOSPlatform("windows")]
    public void Windows_a_file_that_only_the_user_and_the_administrators_can_change_is_secure()
    {
        RequireWindows();
        var file = NewFile();
        SetRules(file, new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.Modify, AccessControlType.Allow),
            Allow(WellKnownSidType.BuiltinAdministratorsSid, FileSystemRights.FullControl),
            Allow(WellKnownSidType.LocalSystemSid, FileSystemRights.FullControl));

        var secure = SecureFile.Read(file);

        Assert.False(secure.IsInsecure(Me));
        Assert.Null(secure.Problem(Me));
    }

    [SkippableFact]
    [SupportedOSPlatform("windows")]
    public void Windows_a_file_that_everyone_can_change_is_insecure_and_the_problem_names_them()
    {
        RequireWindows();
        var file = NewFile();
        SetRules(file, new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, AccessControlType.Allow),
            Allow(WellKnownSidType.WorldSid, FileSystemRights.Modify));

        var secure = SecureFile.Read(file);

        Assert.True(secure.IsInsecure(Me));
        Assert.Contains("Everyone", secure.Problem(Me));
    }

    [SkippableFact]
    [SupportedOSPlatform("windows")]
    public void Windows_the_file_is_locked_against_changes_while_it_is_read_and_free_again_afterwards()
    {
        RequireWindows();
        var file = NewFile();

        // While a SecureFile has the file open (FileShare.Read, no Delete), writing, renaming and deleting it fail.
        using (var open = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.Throws<IOException>(() => File.Delete(file));
            Assert.Throws<IOException>(() => File.Move(file, file + ".moved"));
            Assert.Throws<IOException>(() => File.WriteAllText(file, "{}"));
        }

        SecureFile.Read(file);
        File.WriteAllText(file, "{ \"d\": 4 }");
        Assert.Equal("{ \"d\": 4 }", SecureFile.Read(file).Text);
    }
}
