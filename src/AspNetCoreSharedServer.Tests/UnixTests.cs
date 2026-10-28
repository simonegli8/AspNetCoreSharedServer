using Xunit;

namespace AspNetCoreSharedServer.Tests;

/// <summary>
/// Unix.IsUserInGroup decides which groups an app owner may give their app. The tests read /etc/passwd and /etc/group themselves
/// to pick real users and groups, so they work on any Linux machine. They are skipped elsewhere.
/// </summary>
public class UnixTests
{
    record PasswdEntry(string Name, uint Gid);
    record GroupEntry(string Name, uint Gid, string[] Members);

    static PasswdEntry[] Passwd() =>
        File.ReadAllLines("/etc/passwd")
            .Select(line => line.Split(':'))
            .Where(parts => parts.Length >= 4 && uint.TryParse(parts[3], out _))
            .Select(parts => new PasswdEntry(parts[0], uint.Parse(parts[3])))
            .ToArray();

    static GroupEntry[] Groups() =>
        File.ReadAllLines("/etc/group")
            .Select(line => line.Split(':'))
            .Where(parts => parts.Length >= 4 && uint.TryParse(parts[2], out _))
            .Select(parts => new GroupEntry(parts[0], uint.Parse(parts[2]), parts[3].Split(',', StringSplitOptions.RemoveEmptyEntries)))
            .ToArray();

    static void RequireLinux() => Skip.IfNot(OperatingSystem.IsLinux(), "Unix.IsUserInGroup is only used on Linux and macOS.");

    [SkippableFact]
    public void Knows_which_users_and_groups_exist()
    {
        RequireLinux();

        Assert.True(Unix.UserExists("root"));
        Assert.True(Unix.GroupExists(Groups().First().Name));
        Assert.False(Unix.UserExists("no-such-user-xyz"));
        Assert.False(Unix.GroupExists("no-such-group-xyz"));
        Assert.False(Unix.UserExists(""));
        Assert.False(Unix.GroupExists(""));
    }

    [SkippableFact]
    public void Leaves_numeric_ids_to_sudo()
    {
        RequireLinux();

        // "#1001" is how sudo names a user or group by number. It resolves that itself, so it counts as existing here.
        Assert.True(Unix.UserExists("#1001"));
        Assert.True(Unix.GroupExists("#1001"));
        // Anything else that starts with # is just a name that does not exist.
        Assert.False(Unix.UserExists("#"));
        Assert.False(Unix.UserExists("#12x"));
    }

    [SkippableFact]
    public void A_user_is_in_the_primary_group_of_their_account()
    {
        RequireLinux();
        var groups = Groups();
        var user = Passwd().FirstOrDefault(entry => groups.Any(group => group.Gid == entry.Gid));
        Skip.If(user == null, "No account with a named primary group.");

        var primary = groups.First(group => group.Gid == user!.Gid);

        Assert.True(Unix.IsUserInGroup(user!.Name, primary.Name));
    }

    [SkippableFact]
    public void A_user_is_in_a_group_that_lists_them_as_a_member()
    {
        RequireLinux();
        var passwd = Passwd();
        var membership = (from entry in Groups()
                          from member in entry.Members
                          let account = passwd.FirstOrDefault(candidate => candidate.Name == member)
                          where account != null && account.Gid != entry.Gid
                          select (Group: entry, Account: account)).FirstOrDefault();
        Skip.If(membership.Group == null, "No account is a supplementary member of any group on this machine.");

        Assert.True(Unix.IsUserInGroup(membership.Account!.Name, membership.Group!.Name));
    }

    [SkippableFact]
    public void A_user_is_not_in_a_group_that_neither_is_their_primary_group_nor_lists_them()
    {
        RequireLinux();
        var groups = Groups();
        var user = Passwd().First();
        var stranger = groups.FirstOrDefault(group => group.Gid != user.Gid && !group.Members.Contains(user.Name));
        Skip.If(stranger == null, "Every group on this machine includes the first account.");

        Assert.False(Unix.IsUserInGroup(user.Name, stranger!.Name));
    }

    [SkippableFact]
    public void Unknown_users_and_groups_are_never_members()
    {
        RequireLinux();
        var user = Passwd().First().Name;
        var group = Groups().First().Name;

        Assert.False(Unix.IsUserInGroup("no-such-user-xyz", group));
        Assert.False(Unix.IsUserInGroup(user, "no-such-group-xyz"));
        Assert.False(Unix.IsUserInGroup(user, ""));
    }

    [SkippableFact]
    public void The_numeric_group_syntax_of_sudo_is_not_accepted()
    {
        RequireLinux();
        // sudo understands "#0" as group 0, which would let an app owner name root's group. It is not a group name, so it is refused.
        Assert.False(Unix.IsUserInGroup(Passwd().First().Name, "#0"));
    }
}
