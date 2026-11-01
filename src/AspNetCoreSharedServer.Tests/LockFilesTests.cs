using EstrellasDeEsperanza.AsyncLock;
using Xunit;
using Mode = System.IO.UnixFileMode;

namespace AspNetCoreSharedServer.Tests;

/// <summary>
/// The lock files of the server are inside its configuration folder, which only root (SYSTEM, the administrators) and the admin group can
/// enter, and not in the folder that every user can write to: a lock is held by whoever can open its file, so a lock file that every
/// user can open is a lock that every user can hold (a denial of service). The tests use a temporary configuration folder.
/// </summary>
[Collection("Working directory")] // the folder is a static setting of the process
public class LockFilesTests : IDisposable
{
    readonly string? previous = Configuration.ConfigDirectoryOverride;
    readonly string folder = Path.Combine(Path.GetTempPath(), "aspsrv-lock-" + Guid.NewGuid().ToString("N"));

    public LockFilesTests()
    {
        Directory.CreateDirectory(folder);
        Configuration.ConfigDirectoryOverride = folder;
    }

    public void Dispose()
    {
        Configuration.ConfigDirectoryOverride = previous;
        // A test may have taken the permissions of the folder away: the folder cannot be listed until they are back.
        if (!OperatingSystem.IsWindows())
        {
            try { File.SetUnixFileMode(folder, Mode.UserRead | Mode.UserWrite | Mode.UserExecute); } catch (Exception) { }
        }
        if (!Directory.Exists(folder)) return;
        foreach (var path in Directory.GetFileSystemEntries(folder, "*", SearchOption.AllDirectories).Append(folder))
        {
            if (OperatingSystem.IsWindows()) continue;
            try { File.SetUnixFileMode(path, Mode.UserRead | Mode.UserWrite | Mode.UserExecute); } catch (Exception) { }
        }
        Directory.Delete(folder, true);
    }

    static void RequireUnix() => Skip.If(OperatingSystem.IsWindows(), "Unix permissions.");

    static void RequireOrdinaryUser()
    {
        RequireUnix();
        Skip.If(Unix.IsRoot, "Root ignores file permissions.");
    }

    [Fact]
    public void The_lock_file_is_in_the_configuration_folder_and_not_in_the_shared_lock_folder()
    {
        var path = LockFiles.PathFor("Test.Lock");

        Assert.Equal(Path.Combine(folder, "Test.Lock.lock"), path);
        Assert.True(File.Exists(path));
        Assert.DoesNotContain("AsyncMutexLock", path);
    }

    [Fact]
    public void A_lock_file_does_not_look_like_an_app_file()
    {
        // The folder is searched for *.json files, and watched for them
        Assert.EndsWith(".lock", LockFiles.PathFor("Test.Lock"));
    }

    [Fact]
    public void Two_locks_on_the_same_file_exclude_each_other_and_let_go_again()
    {
        var path = LockFiles.PathFor("Test.Lock");
        var first = new AsyncMutexLock(path);
        var second = new AsyncMutexLock(path);

        using (first.Lock(TimeSpan.Zero))
        {
            Assert.Throws<TimeoutException>(() => second.Lock(TimeSpan.Zero));
        }
        using (second.Lock(TimeSpan.Zero)) { }
    }

    [Fact]
    public void The_lock_of_the_configuration_follows_the_configuration_folder()
    {
        var other = Path.Combine(Path.GetTempPath(), "aspsrv-lock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(other);
        try
        {
            var inFolder = Configuration.MutexLock;
            Assert.Same(inFolder, Configuration.MutexLock); // not made again while the folder stays

            using (inFolder.Lock(TimeSpan.Zero))
            {
                Configuration.ConfigDirectoryOverride = other;
                var inOther = Configuration.MutexLock;

                Assert.NotSame(inFolder, inOther);
                using (inOther.Lock(TimeSpan.Zero)) { } // another folder is another server: no competition
            }
        }
        finally { Directory.Delete(other, true); }
    }

    [SkippableFact]
    public void Unix_the_lock_file_is_for_its_owner_and_the_group_and_nobody_else()
    {
        RequireUnix();

        var path = LockFiles.PathFor("Test.Lock");

        Assert.Equal(Mode.UserRead | Mode.UserWrite | Mode.GroupRead | Mode.GroupWrite, File.GetUnixFileMode(path));
    }

    [SkippableFact]
    public void Unix_a_lock_file_that_an_older_version_made_world_readable_is_repaired()
    {
        RequireUnix();
        var path = Path.Combine(folder, "Test.Lock.lock");
        File.WriteAllText(path, "");
        File.SetUnixFileMode(path, Mode.UserRead | Mode.UserWrite | Mode.GroupRead | Mode.OtherRead | Mode.OtherWrite); // anybody could lock it

        LockFiles.PathFor("Test.Lock");

        Assert.Equal(Mode.UserRead | Mode.UserWrite | Mode.GroupRead | Mode.GroupWrite, File.GetUnixFileMode(path));
    }

    [SkippableFact]
    public void Unix_a_symbolic_link_in_place_of_the_lock_file_is_replaced_and_its_target_is_left_alone()
    {
        RequireUnix();
        var target = Path.Combine(Path.GetTempPath(), "aspsrv-target-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(target, "content");
        File.SetUnixFileMode(target, Mode.UserRead | Mode.UserWrite | Mode.OtherRead);
        var path = Path.Combine(folder, "Test.Lock.lock");
        try
        {
            File.CreateSymbolicLink(path, target);

            LockFiles.PathFor("Test.Lock");

            Assert.Equal("content", File.ReadAllText(target));
            Assert.Equal(Mode.UserRead | Mode.UserWrite | Mode.OtherRead, File.GetUnixFileMode(target)); // not changed through the link
            Assert.Null(new FileInfo(path).LinkTarget); // a file of its own now
        }
        finally { File.Delete(target); }
    }

    [SkippableFact]
    public void Unix_somebody_who_may_not_enter_the_folder_gets_told_so_and_not_that_another_instance_runs()
    {
        RequireOrdinaryUser();
        File.SetUnixFileMode(folder, Mode.None); // what a user outside the admin group sees of /etc/aspnet-server

        var ex = Assert.Throws<UnauthorizedAccessException>(() => LockFiles.PathFor("Test.Lock"));

        Assert.Contains("member of the group aspnet-server", ex.Message);
        Assert.Contains("Test.Lock.lock", ex.Message);
    }
}
