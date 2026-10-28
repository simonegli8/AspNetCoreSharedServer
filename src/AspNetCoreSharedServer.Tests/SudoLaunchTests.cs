using System.Diagnostics;
using Xunit;

namespace AspNetCoreSharedServer.Tests;

/// <summary>
/// Starts a real process as another user with the command line that the server builds, and looks at it from the inside.
/// This is the test that protects the privilege drop: an app must become the other user completely and must not be able to
/// switch back to root. (Switching only the effective user of the server around Process.Start leaves the real user as root:
/// the app can then call seteuid(0) and has root again.)
///
/// It needs root, because only root can start a process as another user: run it with <c>sudo dotnet test</c> on Linux or macOS.
/// Without root it is reported as skipped.
/// </summary>
public class SudoLaunchTests
{
    const string User = "nobody";
    const string Perl = "/usr/bin/perl"; // present on Linux and macOS, and can tell its own user ids and try to switch them

    static void RequirePrerequisites()
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), "sudo is how the server drops privileges on Linux and macOS.");
        Skip.IfNot(Unix.IsRoot, "Only root can start a process as another user. Run: sudo dotnet test");
        Skip.IfNot(new[] { "/usr/bin/sudo", "/usr/local/bin/sudo", "/bin/sudo" }.Any(File.Exists), "sudo is not installed.");
        Skip.IfNot(File.Exists(Perl), "perl is the probe program of this test.");
        Skip.IfNot(Unix.UserExists(User), $"There is no user '{User}' to drop privileges to.");
    }

    static string Capture(string fileName, params string[] arguments)
    {
        var info = new ProcessStartInfo(fileName) { RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        return output;
    }

    /// <summary>The program the app stands in for: reports its own ids, then does what a malicious app would try.</summary>
    const string Probe = """
        my ($real, $effective) = ($<, $>);
        $> = 0;   # if this process were still root underneath, this would give root back
        my $regained = ($> == 0) ? 1 : 0;
        my $gid = $( + 0;
        print join("|", "real=$real", "effective=$effective", "regained=$regained", "gid=$gid",
            "home=$ENV{HOME}", "user=$ENV{USER}", "logname=$ENV{LOGNAME}", "foo=$ENV{FOO}"), "\n";
        """;

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_app_is_the_other_user_through_and_through_and_cannot_switch_back_to_root(bool withGroup)
    {
        RequirePrerequisites();
        var uid = Capture("id", "-u", User);
        var gid = Capture("id", "-g", User);
        var group = Capture("id", "-gn", User);
        var home = Capture("/bin/sh", "-c", $"echo ~{User}"); // the home directory from the password database
        var rootHome = Environment.GetEnvironmentVariable("HOME");

        var command = $"{Server.QuoteArg(Perl)} -e {Server.QuoteArg(Probe)}";
        var info = new ProcessStartInfo("sudo")
        {
            Arguments = Server.CreateSudoArguments(User, withGroup ? group : null, new Dictionary<string, string> { ["FOO"] = "bar baz" }, command),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd().Trim();
        var error = process.StandardError.ReadToEnd().Trim();
        process.WaitForExit();

        Assert.True(process.ExitCode == 0, $"sudo failed with exit code {process.ExitCode}: {error}");
        var report = output.Split('|').Select(part => part.Split('=', 2)).ToDictionary(part => part[0], part => part[1]);
        // The real user is the other user, not only the effective one: nothing of root is left.
        Assert.Equal(uid, report["real"]);
        Assert.Equal(uid, report["effective"]);
        Assert.Equal("0", report["regained"]);
        Assert.Equal(gid, report["gid"]);
        // The environment is that of the other user, with the variables of the app passed on.
        Assert.Equal(home, report["home"]);
        if (rootHome != home) Assert.NotEqual(rootHome, report["home"]);
        Assert.Equal(User, report["user"]);
        Assert.Equal(User, report["logname"]);
        Assert.Equal("bar baz", report["foo"]);
    }
}
