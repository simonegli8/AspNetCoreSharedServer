using Xunit;

namespace AspNetCoreSharedServer.Tests;

/// <summary>
/// Where the configuration lives: the fixed folder of the platform, or the folder of the command line option
/// <c>--config-dir</c> on every platform. The environment is never consulted: a variable of the old name does nothing. Nothing here reads
/// or writes the real folder: the tests only look at paths, and at a legacy file in a temporary folder.
/// </summary>
[Collection("Working directory")] // the folder is a static setting of the process
public class ConfigDirectoryTests : IDisposable
{
    const string OldVariable = "ASPNETSERVER_CONFIG_DIR";
    readonly string? previousFolder = Configuration.ConfigDirectoryOverride;
    readonly string? previousVariable = Environment.GetEnvironmentVariable(OldVariable);
    readonly string folder = Path.Combine(Path.GetTempPath(), "aspsrv-dir-" + Guid.NewGuid().ToString("N"));

    public ConfigDirectoryTests() => Configuration.ConfigDirectoryOverride = null;

    public void Dispose()
    {
        Configuration.ConfigDirectoryOverride = previousFolder;
        Environment.SetEnvironmentVariable(OldVariable, previousVariable);
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
    }

    static string DefaultPath => OperatingSystem.IsWindows() ?
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "aspnet-server", "configuration.json") :
        "/etc/aspnet-server/configuration.json";

    [Fact]
    public void Without_the_option_the_configuration_is_in_the_fixed_folder()
    {
        Assert.Equal(DefaultPath, new Configuration().ConfigPath);
    }

    [Fact]
    public void The_environment_variable_of_the_old_version_does_nothing()
    {
        Environment.SetEnvironmentVariable(OldVariable, folder);

        Assert.Equal(DefaultPath, new Configuration().ConfigPath);
    }

    [Fact]
    public void The_option_moves_the_configuration_on_every_platform()
    {
        var rest = Configuration.TakeConfigDirectory(new[] { "--config-dir", folder });

        var configuration = new Configuration();
        Assert.Empty(rest);
        Assert.Equal(Path.Combine(folder, "configuration.json"), configuration.ConfigPath);
        Assert.Equal(Path.Combine(folder, "applications.json"), configuration.ConfigPathOld);
    }

    [Fact]
    public void The_folder_can_be_joined_to_the_option_with_an_equals_sign()
    {
        var rest = Configuration.TakeConfigDirectory(new[] { "--config-dir=" + folder });

        Assert.Empty(rest);
        Assert.Equal(Path.Combine(folder, "configuration.json"), new Configuration().ConfigPath);
    }

    [Fact]
    public void The_option_can_be_anywhere_and_the_other_arguments_stay_in_order()
    {
        var rest = Configuration.TakeConfigDirectory(new[] { "stop", "--CONFIG-DIR", folder, "TestApp" });

        Assert.Equal(new[] { "stop", "TestApp" }, rest);
        Assert.Equal(Path.Combine(folder, "configuration.json"), new Configuration().ConfigPath);
    }

    [Fact]
    public void Arguments_without_the_option_come_back_unchanged_and_the_folder_stays_fixed()
    {
        var args = new[] { "status", "all", "--config-directory-not", "x" };

        Assert.Equal(args, Configuration.TakeConfigDirectory(args));
        Assert.Equal(DefaultPath, new Configuration().ConfigPath);
    }

    [Fact]
    public void A_relative_folder_becomes_a_full_path()
    {
        Configuration.TakeConfigDirectory(new[] { "--config-dir", Path.Combine("some", "folder") });

        Assert.True(Path.IsPathRooted(new Configuration().ConfigPath));
        Assert.EndsWith(Path.Combine("some", "folder", "configuration.json"), new Configuration().ConfigPath);
    }

    public static TheoryData<string[]> OptionsWithoutAFolder => new()
    {
        new[] { "--config-dir" },
        new[] { "status", "--config-dir" },
        new[] { "--config-dir=" },
        new[] { "--config-dir", "" },
    };

    [Theory]
    [MemberData(nameof(OptionsWithoutAFolder))]
    public void The_option_without_a_folder_is_an_error_and_changes_nothing(string[] args)
    {
        Assert.Throws<ArgumentException>(() => Configuration.TakeConfigDirectory(args));

        Assert.Equal(DefaultPath, new Configuration().ConfigPath);
    }

    [Fact]
    public void A_legacy_file_next_to_the_configuration_is_not_imported_and_its_folder_is_not_deleted()
    {
        Configuration.TakeConfigDirectory(new[] { "--config-dir", folder });
        Directory.CreateDirectory(folder);
        var legacy = Path.Combine(folder, "applications.json");
        File.WriteAllText(legacy, "{}");

        // Importing the old configuration deletes the folder it came from, which here is the folder of the current one.
        Assert.False(new Configuration().HasOldConfig);
        Assert.True(File.Exists(legacy));
    }

    [SkippableFact]
    public void The_legacy_configuration_of_the_fixed_folder_is_in_a_folder_of_its_own_on_linux_and_macos()
    {
        Skip.If(OperatingSystem.IsWindows(), "On Windows it is in the folder of the configuration.");

        Assert.Equal("/etc/aspnetcore/applications.json", new Configuration().ConfigPathOld);
    }
}
