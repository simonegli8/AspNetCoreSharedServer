using System.Diagnostics;
using Xunit;

namespace AspNetCoreSharedServer.Tests;

/// <summary>The Alt-Svc header the server hands to the app, and how it travels to it (environment and command line).</summary>
public class AltSvcTests
{
    [Theory]
    [InlineData(443, "h3=\":443\"; ma=86400")]
    [InlineData(8443, "h3=\":8443\"; ma=86400")]
    public void Advertises_HTTP3_on_the_public_HTTPS_port(int port, string expected)
    {
        Assert.Equal(expected, Server.CreateAltSvcHeader(http3Enabled: true, hasHttps: true, publicHttpsPort: port));
    }

    [Theory]
    [InlineData(false, true, 443)]  // HTTP/3 is not forwarded
    [InlineData(true, false, 443)]  // no HTTPS listener: HTTP/3 needs TLS
    [InlineData(true, true, -1)]    // port not known yet
    [InlineData(true, true, 0)]
    public void Is_empty_when_HTTP3_is_not_forwarded(bool enabled, bool hasHttps, int port)
    {
        Assert.Equal("", Server.CreateAltSvcHeader(enabled, hasHttps, port));
    }

    [Fact]
    public void Uses_the_documented_variable_names()
    {
        Assert.Equal("HTTP3_ALT_SVC_HEADER", Server.AltSvcVariable);
        Assert.Equal("HTTP3_ENABLED", Server.Http3EnabledVariable);
    }

    /// <summary>Runs the test assembly as a helper program and returns its output lines.</summary>
    static (string[] Lines, Process Process) RunHelper(string arguments, Action<ProcessStartInfo>? configure = null) =>
        HelperProcess.Run(arguments, configure);

    [Fact]
    public void Quoted_arguments_arrive_as_exactly_one_argument_each()
    {
        // The sudo launch path builds a command line with these values in front of "--". Nothing in them may split or merge.
        var values = new[]
        {
            "plain", "with space", "a\"b", "trailing\\", "back\\\\\"q", "C:\\dir with space\\", "",
            "ASPNETCORE_URLS=http://127.0.0.1:5;http://[::1]:5", "-u root", "x -- /bin/sh -c evil", "tab\there",
            $"{Server.AltSvcVariable}={Server.CreateAltSvcHeader(true, true, 443)}",
        };

        var (lines, _) = RunHelper("--echo " + string.Join(" ", values.Select(Server.QuoteArg)));

        Assert.Equal(values.Select(value => "[" + value + "]"), lines);
    }

    [Fact]
    public void The_header_value_reaches_a_child_process_through_the_environment()
    {
        var value = Server.CreateAltSvcHeader(true, true, 443);

        var (lines, _) = RunHelper($"--env {Server.AltSvcVariable}", info => info.Environment[Server.AltSvcVariable] = value);

        Assert.Equal(new[] { value }, lines);
    }
}
