using System.Text;
using Xunit;

namespace AspNetCoreSharedServer.Tests;

/// <summary>
/// The Windows counterpart of UnixTests: local users and groups, the environment block of an app, and the check for the privileges
/// that an S4U launch needs. The queries are read-only and use accounts that every Windows machine has, so they run on any Windows
/// machine and without administrator rights. They are skipped elsewhere.
/// </summary>
public class WindowsAccountsTests
{
    static void RequireWindows() => Skip.IfNot(OperatingSystem.IsWindows(), "These tests use the local accounts of Windows.");

    [SkippableTheory]
    [InlineData("alice", "alice")]
    [InlineData(".\\alice", "alice")]
    [InlineData("alice.smith", "alice.smith")]
    public void A_local_account_name_loses_its_machine_prefix(string account, string expected)
    {
        RequireWindows();
        Assert.Equal(expected, WindowsAccounts.LocalName(account));
    }

    [SkippableFact]
    public void The_name_of_this_machine_is_a_local_prefix_and_a_domain_is_not()
    {
        RequireWindows();
        Assert.Equal("alice", WindowsAccounts.LocalName(Environment.MachineName + "\\alice"));
        Assert.Null(WindowsAccounts.LocalName("EXAMPLE\\alice"));
        Assert.Null(WindowsAccounts.LocalName(""));
        Assert.Null(WindowsAccounts.LocalName("  "));
    }

    [SkippableFact]
    public void Knows_which_users_and_groups_exist()
    {
        RequireWindows();
        Assert.True(WindowsAccounts.UserExists(Environment.UserName));
        Assert.True(WindowsAccounts.GroupExists("Users"));
        Assert.True(WindowsAccounts.GroupExists("Administrators"));
        Assert.False(WindowsAccounts.UserExists("no-such-user-xyz"));
        Assert.False(WindowsAccounts.GroupExists("no-such-group-xyz"));
        // An account of a domain is not looked up on this machine.
        Assert.False(WindowsAccounts.UserExists("EXAMPLE\\" + Environment.UserName));
    }

    [SkippableFact]
    public void A_user_is_in_a_group_that_it_belongs_to_and_not_in_one_that_it_does_not()
    {
        RequireWindows();
        // Every local account is a member of Users, directly or through "Authenticated Users"; Guest is not an administrator.
        Skip.IfNot(WindowsAccounts.UserExists("Guest"), "No Guest account on this machine.");
        Assert.False(WindowsAccounts.IsUserInGroup("Guest", "Administrators"));
        Assert.False(WindowsAccounts.IsUserInGroup("no-such-user-xyz", "Users"));
        Assert.False(WindowsAccounts.IsUserInGroup("Guest", "no-such-group-xyz"));
    }

    [SkippableFact]
    public void The_administrator_group_lists_the_built_in_administrator()
    {
        RequireWindows();
        Skip.IfNot(WindowsAccounts.UserExists("Administrator"), "No Administrator account on this machine.");
        Assert.True(WindowsAccounts.IsUserInGroup("Administrator", "Administrators"));
        Assert.True(WindowsAccounts.IsUserInGroup(".\\Administrator", "administrators")); // case does not matter
    }

    [SkippableFact]
    public void The_current_account_is_recognised()
    {
        RequireWindows();
        Assert.True(WindowsLaunch.IsCurrentUser(Environment.UserName));
        Assert.True(WindowsLaunch.IsCurrentUser(".\\" + Environment.UserName));
        Assert.False(WindowsLaunch.IsCurrentUser("no-such-user-xyz"));
        Assert.False(WindowsLaunch.IsCurrentUser("EXAMPLE\\" + Environment.UserName));
    }

    [SkippableFact]
    public void A_random_password_meets_a_complexity_policy_and_is_different_each_time()
    {
        RequireWindows();
        var first = WindowsAccounts.RandomPassword();
        var second = WindowsAccounts.RandomPassword();

        Assert.NotEqual(first, second);
        Assert.True(first.Length >= 32);
        Assert.Contains(first, char.IsUpper);
        Assert.Contains(first, char.IsLower);
        Assert.Contains(first, char.IsDigit);
        Assert.Contains(first, c => !char.IsLetterOrDigit(c));
    }

    [SkippableFact]
    public void The_environment_of_the_app_is_its_users_with_the_server_variables_on_top()
    {
        RequireWindows();
        var user = new Dictionary<string, string> { ["PATH"] = "C:\\Windows", ["USERNAME"] = "app1", ["ASPNET_SERVER"] = "from-the-user" };
        var server = new Dictionary<string, string> { ["ASPNET_SERVER"] = "4.0.1", ["Path"] = "C:\\Tools", ["HTTP3_ENABLED"] = "false" };

        var block = Encoding.Unicode.GetString(WindowsLaunch.BuildEnvironmentBlock(user, server));
        var entries = block.Split('\0', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(new[] { "ASPNET_SERVER=4.0.1", "HTTP3_ENABLED=false", "PATH=C:\\Tools", "USERNAME=app1" }, entries); // names are case-insensitive; the user's spelling stays
        Assert.EndsWith("\0\0", block); // the block ends with an empty entry
    }

    [SkippableFact]
    public void Without_the_privileges_of_an_S4U_launch_the_missing_ones_are_named()
    {
        RequireWindows();
        var message = WindowsLaunch.MissingPrivileges();

        // On a machine where this test runs as SYSTEM nothing is missing; otherwise the message names what to grant.
        if (message != null)
        {
            Assert.Contains("SeTcbPrivilege", message);
            Assert.Contains("LocalSystem", message);
        }
    }
}
