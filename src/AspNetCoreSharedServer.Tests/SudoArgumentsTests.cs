using Xunit;

namespace AspNetCoreSharedServer.Tests;

/// <summary>
/// The command line with which the server starts an app as another user (on Linux and on macOS): <c>-H -u user [-g group]
/// NAME=value... -- command</c>. The tests let the real argument parser split it, so they show what sudo would receive.
/// </summary>
public class SudoArgumentsTests
{
    static IReadOnlyDictionary<string, string> Environment(params (string Name, string Value)[] variables) =>
        variables.ToDictionary(variable => variable.Name, variable => variable.Value);

    /// <summary>Splits the arguments into those in front of the first "--" and the command behind it.</summary>
    static (string[] Options, string[] Command) Split(string commandLine)
    {
        var arguments = HelperProcess.Arguments(commandLine);
        var separator = Array.IndexOf(arguments, "--");
        Assert.True(separator >= 0, "there is no -- in " + commandLine);
        return (arguments[..separator], arguments[(separator + 1)..]);
    }

    [Fact]
    public void Has_the_shape_sudo_expects()
    {
        var commandLine = Server.CreateSudoArguments("www-data", "web", Environment(("FOO", "bar baz")), "\"/usr/bin/app\" --port 80");

        var (options, command) = Split(commandLine);

        Assert.Equal(new[] { "-H", "-u", "www-data", "-g", "web" }, options.Take(5));
        Assert.Equal(new[] { "/usr/bin/app", "--port", "80" }, command);
        // Then the variables, each as one NAME=value argument.
        Assert.Equal(new[] { "FOO=bar baz", "LOGNAME=www-data", "USER=www-data" }, options.Skip(5).OrderBy(variable => variable));
    }

    [Fact]
    public void Asks_sudo_to_set_HOME_for_the_target_user()
    {
        // HOME comes from the password database that sudo reads itself (-H), not from the root environment of the server.
        var (options, _) = Split(Server.CreateSudoArguments("www-data", null, Environment(), "/bin/true"));

        Assert.Equal("-H", options[0]);
    }

    [Fact]
    public void Leaves_out_the_group_option_when_there_is_no_group()
    {
        var (options, _) = Split(Server.CreateSudoArguments("www-data", null, Environment(), "/bin/true"));

        Assert.DoesNotContain("-g", options);
    }

    [Fact]
    public void Names_the_target_user_in_USER_and_LOGNAME()
    {
        var (options, _) = Split(Server.CreateSudoArguments("www-data", null, Environment(), "/bin/true"));

        Assert.Contains("USER=www-data", options);
        Assert.Contains("LOGNAME=www-data", options);
    }

    [Fact]
    public void Lets_the_app_define_its_own_USER_and_LOGNAME()
    {
        var (options, _) = Split(Server.CreateSudoArguments("www-data", null, Environment(("USER", "custom")), "/bin/true"));

        Assert.Contains("USER=custom", options);
        Assert.DoesNotContain("USER=www-data", options);
        Assert.Contains("LOGNAME=www-data", options);
    }

    [Fact]
    public void Does_not_invent_USER_for_a_user_given_by_number()
    {
        var (options, _) = Split(Server.CreateSudoArguments("#1001", null, Environment(), "/bin/true"));

        Assert.Equal(new[] { "-H", "-u", "#1001" }, options);
    }

    [Fact]
    public void Ignores_variables_with_names_that_cannot_be_environment_variables()
    {
        var (options, _) = Split(Server.CreateSudoArguments("www-data", null,
            Environment(("GOOD_1", "yes"), ("BAD NAME", "no"), ("1BAD", "no"), ("A=B", "no"), ("", "no")), "/bin/true"));

        Assert.Contains("GOOD_1=yes", options);
        Assert.Equal(1 + 2, options.Count(argument => argument.Contains('=')) ); // GOOD_1, USER, LOGNAME: nothing else got through
    }

    [Fact]
    public void Keeps_hostile_values_inside_their_own_argument()
    {
        // Values come from files that the owner of an app can edit: a space, a quote or a "--" must not add options or end them early.
        var commandLine = Server.CreateSudoArguments("a user", "g\"roup", Environment(("X", "1 -u root -- /bin/sh -c evil"), ("Y", "\"; rm -rf /")),
            "\"/usr/bin/app\"");

        var (options, command) = Split(commandLine);

        Assert.Equal(new[] { "-H", "-u", "a user", "-g", "g\"roup" }, options.Take(5));
        Assert.Contains("X=1 -u root -- /bin/sh -c evil", options);
        Assert.Contains("Y=\"; rm -rf /", options);
        Assert.Equal(new[] { "/usr/bin/app" }, command); // the command is still only what the server put behind the first "--"
    }
}
