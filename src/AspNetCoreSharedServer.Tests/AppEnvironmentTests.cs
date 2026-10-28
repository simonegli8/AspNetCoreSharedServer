using Xunit;

namespace AspNetCoreSharedServer.Tests;

/// <summary>
/// The environment variables that the server gives an app: its own configuration, and the ones the server defines (the urls, the HTTP/3
/// variables, PROXY_PROTOCOL and ASPNET_SERVER), which an app cannot override.
/// </summary>
public class AppEnvironmentTests
{
    static Dictionary<string, string> Create(IReadOnlyDictionary<string, string>? app = null, string proxyProtocol = "") =>
        Server.CreateEnvironment(app, "https://example.org", "https://127.0.0.1:5001", "", proxyProtocol);

    // --- which PROXY protocol version is sent ---

    [Theory]
    [InlineData(null, null, "")]
    [InlineData(false, false, "")]
    [InlineData(true, null, "v1")]
    [InlineData(true, false, "v1")]
    [InlineData(null, true, "v2")]
    [InlineData(false, true, "v2")]
    [InlineData(true, true, "v2")] // when both are enabled the server sends v2
    public void The_PROXY_protocol_version_follows_the_two_options(bool? enableProxyHeader, bool? enableProxyV2Header, string expected)
    {
        Assert.Equal(expected, Server.CreateProxyProtocolVersion(enableProxyHeader, enableProxyV2Header));
    }

    [Fact]
    public void The_variable_has_the_documented_name()
    {
        Assert.Equal("PROXY_PROTOCOL", Server.ProxyProtocolVariable);
    }

    // --- PROXY_PROTOCOL in the environment ---

    [Theory]
    [InlineData("v1")]
    [InlineData("v2")]
    public void PROXY_PROTOCOL_is_set_when_the_server_sends_a_PROXY_header(string version)
    {
        var environment = Create(proxyProtocol: version);

        Assert.Equal(version, environment[Server.ProxyProtocolVariable]);
    }

    [Fact]
    public void PROXY_PROTOCOL_is_not_set_when_the_server_sends_no_PROXY_header()
    {
        Assert.DoesNotContain(Server.ProxyProtocolVariable, Create().Keys);
    }

    [Fact]
    public void A_PROXY_PROTOCOL_from_the_configuration_of_the_app_is_removed_when_the_server_sends_no_header()
    {
        // It would claim a header that does not come.
        var environment = Create(new Dictionary<string, string> { [Server.ProxyProtocolVariable] = "v2" });

        Assert.DoesNotContain(Server.ProxyProtocolVariable, environment.Keys);
    }

    [Fact]
    public void A_PROXY_PROTOCOL_from_the_configuration_of_the_app_cannot_change_what_the_server_sends()
    {
        var environment = Create(new Dictionary<string, string> { [Server.ProxyProtocolVariable] = "v2" }, proxyProtocol: "v1");

        Assert.Equal("v1", environment[Server.ProxyProtocolVariable]);
    }

    // --- the rest of the environment ---

    [Fact]
    public void Keeps_the_variables_of_the_app()
    {
        var environment = Create(new Dictionary<string, string> { ["ASPNETCORE_ENVIRONMENT"] = "Production", ["MY_SETTING"] = "a b" });

        Assert.Equal("Production", environment["ASPNETCORE_ENVIRONMENT"]);
        Assert.Equal("a b", environment["MY_SETTING"]);
    }

    [Fact]
    public void The_variables_of_the_server_win_over_those_of_the_app()
    {
        var environment = Server.CreateEnvironment(
            new Dictionary<string, string>
            {
                ["ORIGINAL_URLS"] = "https://evil.example",
                ["ASPNETCORE_URLS"] = "http://0.0.0.0:80",
                [Server.AltSvcVariable] = "h3=\":1\"",
                [Server.Http3EnabledVariable] = "true",
            },
            "https://example.org", "https://127.0.0.1:5001", altSvc: "", proxyProtocol: "");

        Assert.Equal("https://example.org", environment["ORIGINAL_URLS"]);
        Assert.Equal("https://127.0.0.1:5001", environment["ASPNETCORE_URLS"]);
        Assert.Equal("", environment[Server.AltSvcVariable]);
        Assert.Equal("false", environment[Server.Http3EnabledVariable]);
    }

    [Fact]
    public void Tells_the_app_whether_HTTP3_is_forwarded()
    {
        var environment = Server.CreateEnvironment(null, null, "https://127.0.0.1:5001", altSvc: "h3=\":443\"; ma=86400", proxyProtocol: "");

        Assert.Equal("h3=\":443\"; ma=86400", environment[Server.AltSvcVariable]);
        Assert.Equal("true", environment[Server.Http3EnabledVariable]);
        Assert.Equal("", environment["ORIGINAL_URLS"]);
    }

    // --- the version of the server ---

    [Fact]
    public void Tells_the_app_which_version_of_the_server_it_runs_behind()
    {
        var environment = Create();

        Assert.Equal("ASPNET_SERVER", Server.ServerVariable);
        Assert.Equal(Server.Version, environment["ASPNET_SERVER"]);
        Assert.Matches(@"^\d+\.\d+\.\d+$", environment["ASPNET_SERVER"]); // major.minor.build, like aspnet-server -v
    }

    [Fact]
    public void An_app_cannot_claim_another_version_of_the_server()
    {
        var environment = Create(new Dictionary<string, string> { ["ASPNET_SERVER"] = "99.0.0" });

        Assert.Equal(Server.Version, environment["ASPNET_SERVER"]);
    }

    // --- on the way to the app ---

    [Fact]
    public void PROXY_PROTOCOL_reaches_the_app_on_the_sudo_command_line()
    {
        // On Linux and macOS an app that has a User is started with sudo, which resets the environment: the variables travel as
        // NAME=value arguments in front of the command. The real argument parser splits the command line here.
        var environment = Create(proxyProtocol: "v2");

        var arguments = HelperProcess.Arguments(Server.CreateSudoArguments("www-data", null, environment, "/bin/true"));

        Assert.Contains("PROXY_PROTOCOL=v2", arguments);
    }

    [Fact]
    public void ASPNET_SERVER_reaches_the_app_on_the_sudo_command_line()
    {
        var arguments = HelperProcess.Arguments(Server.CreateSudoArguments("www-data", null, Create(), "/bin/true"));

        Assert.Contains("ASPNET_SERVER=" + Server.Version, arguments);
    }

    [Fact]
    public void PROXY_PROTOCOL_is_not_on_the_sudo_command_line_when_no_header_is_sent()
    {
        var arguments = HelperProcess.Arguments(Server.CreateSudoArguments("www-data", null, Create(), "/bin/true"));

        Assert.DoesNotContain(arguments, argument => argument.StartsWith("PROXY_PROTOCOL="));
    }
}
