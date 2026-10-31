using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Xunit;

namespace AspNetCoreSharedServer.Tests;

/// <summary>Tests that change the configuration folder of the process (an environment variable): nothing else may run meanwhile.</summary>
[CollectionDefinition("Working directory", DisableParallelization = true)]
public class WorkingDirectoryCollection { }

/// <summary>
/// Loading configuration.json with separate app files on Windows: an app file that someone else than the app's User (and the
/// administrators) can change takes its app offline, the way a file with the wrong owner or mode does on Unix.
/// </summary>
[Collection("Working directory")]
[SupportedOSPlatform("windows")]
public class WindowsAppFileLoadTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "aspsrv-load-" + Guid.NewGuid().ToString("N"));
    readonly string? previousConfigDirectory = Environment.GetEnvironmentVariable(Configuration.ConfigDirectoryVariable);

    public WindowsAppFileLoadTests()
    {
        if (!OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(folder);
        // The server reads (and saves) its configuration from this folder instead of %ProgramData%spnet-server.
        Environment.SetEnvironmentVariable(Configuration.ConfigDirectoryVariable, folder);
        // Never go on if the tests could end up in the real configuration: they save, and they change permissions.
        Assert.StartsWith(folder, new Configuration().ConfigPath);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(Configuration.ConfigDirectoryVariable, previousConfigDirectory);
        if (!Directory.Exists(folder)) return;
        try { Directory.Delete(folder, true); }
        catch (UnauthorizedAccessException)
        {
            // The server protected the folder and the files. The owner may always change the permissions again.
            foreach (var path in Directory.GetFileSystemEntries(folder, "*", SearchOption.AllDirectories).Append(folder))
            {
                var security = new FileSecurity();
                security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, AccessControlType.Allow));
                try { new FileInfo(path).SetAccessControl(security); } catch { }
            }
            Directory.Delete(folder, true);
        }
    }

    static void RequireWindows() => Skip.IfNot(OperatingSystem.IsWindows(), "Windows ACLs.");

    string WriteApp(string name, string user, bool protect, params WellKnownSidType[] alsoWritableBy)
    {
        var file = Path.Combine(folder, name + ".json");
        File.WriteAllText(file, $$"""
            { "Name": "{{name}}", "Assembly": "C:\\no\\{{name}}.dll", "ListenUrls": "http://localhost:0", "Urls": "http://example.org", "User": "{{user}}" }
            """);
        if (protect) WindowsFileSecurity.Protect(file, user);
        OwnedByTheUser(file);
        if (alsoWritableBy.Length > 0)
        {
            var security = new FileInfo(file).GetAccessControl();
            foreach (var sid in alsoWritableBy)
            {
                security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.Modify, AccessControlType.Allow));
            }
            new FileInfo(file).SetAccessControl(security);
        }
        return file;
    }

    /// <summary>
    /// A file that an elevated process creates is owned by the administrators, which the server trusts like a file owned by root. The
    /// tests want the file of an ordinary user, so the owner is the account that runs them.
    /// </summary>
    static void OwnedByTheUser(string file)
    {
        var security = new FileInfo(file).GetAccessControl(AccessControlSections.Owner);
        security.SetOwner(WindowsIdentity.GetCurrent().User!);
        new FileInfo(file).SetAccessControl(security);
    }

    string ConfigFile => Path.Combine(folder, "configuration.json");

    /// <summary>
    /// Loads configuration.json from the working directory. The configuration is the one of the account that runs the test, so that its
    /// own security check (the file may only be changed by that user, administrators and SYSTEM) passes for the file that is written here.
    /// </summary>
    Configuration Load(bool protectConfiguration = true)
    {
        File.WriteAllText(ConfigFile, "{ \"Applications\": [] }");
        if (protectConfiguration) WindowsFileSecurity.Protect(ConfigFile, Environment.UserName);
        return new Configuration { User = Environment.UserName }.LoadConfig() ?? throw new InvalidOperationException("The configuration was not loaded.");
    }

    [SkippableFact]
    public void An_app_file_that_only_its_user_and_the_administrators_can_change_loads()
    {
        RequireWindows();
        WriteApp("Good", Environment.UserName, protect: true);

        var app = Load().Applications!["Good"]!;

        Assert.False(app.Offline);
        Assert.Null(app.Error);
    }

    [SkippableTheory]
    [InlineData(WellKnownSidType.BuiltinUsersSid)]
    [InlineData(WellKnownSidType.WorldSid)]
    public void An_app_file_that_a_group_can_change_takes_its_app_offline(WellKnownSidType group)
    {
        RequireWindows();
        WriteApp("Bad", Environment.UserName, protect: true, group);

        var app = Load().Applications!["Bad"]!;

        Assert.True(app.Offline);
        Assert.Equal(Status.Error, app.Status);
        Assert.Contains("secur", app.Error); // "not secure" when loading, "for security reasons" when the configuration is saved again
        Assert.Contains(Environment.UserName, app.Error);
    }

    [SkippableFact]
    public void A_file_for_a_different_user_than_the_one_who_can_change_it_takes_its_app_offline()
    {
        RequireWindows();
        // The file is protected for the account that runs the test, but the app says it runs as somebody else.
        var file = WriteApp("Other", Environment.UserName, protect: true);
        File.WriteAllText(file, File.ReadAllText(file).Replace($"\"User\": \"{Environment.UserName}\"", "\"User\": \"no-such-user-xyz\""));

        var app = Load().Applications!["Other"]!;

        Assert.True(app.Offline);
        Assert.Contains("secur", app.Error); // "not secure" when loading, "for security reasons" when the configuration is saved again
    }

    [SkippableFact]
    public void A_group_that_the_user_does_not_belong_to_takes_the_app_offline()
    {
        RequireWindows();
        // The group of an app has to be one that its user belongs to. Pick a built-in group that this account is not a member of.
        var group = new[] { "Backup Operators", "Performance Log Users", "Event Log Readers", "Print Operators", "Replicator" }
            .FirstOrDefault(name => WindowsAccounts.GroupExists(name) && !WindowsAccounts.IsUserInGroup(Environment.UserName, name));
        Skip.If(group == null, "This account belongs to every group that the test knows.");

        var file = WriteApp("Grouped", Environment.UserName, protect: true);
        File.WriteAllText(file, File.ReadAllText(file).Replace("}", $", \"Group\": \"{group}\" }}"));
        WindowsFileSecurity.Protect(file, Environment.UserName);
        OwnedByTheUser(file);

        var app = Load().Applications!["Grouped"]!;

        Assert.True(app.Offline);
        Assert.Contains(group!, app.Error);
    }

    [SkippableFact]
    public void A_group_that_the_user_belongs_to_is_accepted()
    {
        RequireWindows();
        // Users contains every local account, directly or through Authenticated Users.
        Skip.IfNot(WindowsAccounts.IsUserInGroup(Environment.UserName, "Users"), "This account is not listed in Users.");

        var file = WriteApp("Member", Environment.UserName, protect: true);
        File.WriteAllText(file, File.ReadAllText(file).Replace("}", ", \"Group\": \"Users\" }"));
        WindowsFileSecurity.Protect(file, Environment.UserName);
        OwnedByTheUser(file);

        var app = Load().Applications!["Member"]!;

        Assert.False(app.Offline);
        Assert.Null(app.Error);
    }

    [SkippableFact]
    public void A_configuration_json_that_only_the_user_and_the_administrators_can_change_loads()
    {
        RequireWindows();

        var config = Load();

        Assert.NotNull(config.Applications);
        Assert.Null(config.Error);
    }

    [SkippableTheory]
    [InlineData(WellKnownSidType.BuiltinUsersSid)]
    [InlineData(WellKnownSidType.WorldSid)]
    public void A_configuration_json_that_a_group_can_change_is_not_loaded_and_the_error_is_written_into_it(WellKnownSidType group)
    {
        RequireWindows();
        File.WriteAllText(ConfigFile, "{ \"Applications\": [] }");
        WindowsFileSecurity.Protect(ConfigFile, Environment.UserName);
        var security = new FileInfo(ConfigFile).GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(group, null), FileSystemRights.Modify, AccessControlType.Allow));
        new FileInfo(ConfigFile).SetAccessControl(security);

        var config = new Configuration { User = Environment.UserName }.LoadConfig();

        Assert.Null(config);
        Assert.Contains("For security reasons, the configuration.json", File.ReadAllText(ConfigFile));
    }

    [SkippableFact]
    public void Saving_as_an_administrator_protects_the_folder_and_the_configuration_json()
    {
        RequireWindows();
        Skip.IfNot(WindowsFileSecurity.IsAdministrator(), "Only an administrator sets the permissions, like only root does on Unix.");
        var config = new Configuration { User = Environment.UserName, Applications = new Applications() };

        config.Save(true, false);

        Assert.Null(WindowsFileSecurity.Check(ConfigFile, null)); // nobody but SYSTEM and the administrators may change it
        var rules = new DirectoryInfo(folder).GetAccessControl(AccessControlSections.Access);
        Assert.True(rules.AreAccessRulesProtected);
        var sids = rules.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
            .Select(rule => (SecurityIdentifier)rule.IdentityReference).ToHashSet();
        Assert.Contains(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), sids);
        Assert.Contains(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), sids);
        Assert.DoesNotContain(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), sids);
        Assert.DoesNotContain(new SecurityIdentifier(WellKnownSidType.WorldSid, null), sids);
    }

    [SkippableFact]
    public void Saving_a_configuration_whose_file_others_can_change_reports_it_instead_of_trusting_it()
    {
        RequireWindows();
        File.WriteAllText(ConfigFile, "{ \"Applications\": [] }");
        WindowsFileSecurity.Protect(ConfigFile, Environment.UserName);
        var security = new FileInfo(ConfigFile).GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.Modify, AccessControlType.Allow));
        new FileInfo(ConfigFile).SetAccessControl(security);
        var config = new Configuration { User = Environment.UserName, Applications = new Applications() };

        config.Save(true, false);

        Assert.Contains("For security reasons, the configuration.json", config.Error);
    }
}
