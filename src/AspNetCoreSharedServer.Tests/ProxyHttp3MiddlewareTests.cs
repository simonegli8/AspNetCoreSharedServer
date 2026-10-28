using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AspNetCoreSharedServer.Tests;

/// <summary>
/// UseAspNetCoreSharedServer from AspNetCoreSharedServer.Middleware, run through a real pipeline built with <see cref="ApplicationBuilder"/>.
/// The end-to-end tests cover it behind the real server, through the test app, which uses it.
/// </summary>
public class ProxyHttp3MiddlewareTests
{
    const string Header = "h3=\":443\"; ma=86400";

    /// <summary>Builds a pipeline (configured by the test, ending in a terminal step) and runs one request through it.</summary>
    static async Task<DefaultHttpContext> RunAsync(Action<IApplicationBuilder> configure, DefaultHttpContext? context = null, Action<HttpContext>? terminal = null)
    {
        var builder = new ApplicationBuilder(new ServiceCollection().BuildServiceProvider());
        configure(builder);
        builder.Run(httpContext =>
        {
            terminal?.Invoke(httpContext);
            return Task.CompletedTask;
        });
        var pipeline = builder.Build();

        context ??= new DefaultHttpContext();
        await pipeline(context);
        return context;
    }

    [Fact]
    public async Task Sends_the_header_value_it_is_given()
    {
        var context = await RunAsync(app => app.UseAspNetCoreSharedServer(Header));

        Assert.Equal(Header, context.Response.Headers["Alt-Svc"].Single());
    }

    [Fact]
    public async Task Sets_the_header_before_the_rest_of_the_pipeline_runs()
    {
        string? seen = null;

        await RunAsync(app => app.UseAspNetCoreSharedServer(Header), terminal: context => seen = context.Response.Headers["Alt-Svc"]);

        Assert.Equal(Header, seen);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Adds_nothing_when_there_is_no_value_but_the_pipeline_still_runs(string? value)
    {
        var reached = false;

        var context = await RunAsync(app => app.UseAspNetCoreSharedServer(value), terminal: _ => reached = true);

        Assert.False(context.Response.Headers.ContainsKey("Alt-Svc"));
        Assert.True(reached);
    }

    [Fact]
    public async Task Does_not_add_a_step_to_the_pipeline_when_there_is_no_value()
    {
        // "Safe to leave in" means free: with an empty variable the pipeline is the one without the call.
        var builder = new ApplicationBuilder(new ServiceCollection().BuildServiceProvider());

        var returned = builder.UseAspNetCoreSharedServer(null);

        Assert.Same(builder, returned);
        builder.Run(_ => Task.CompletedTask);
        // A pipeline of just the terminal step: the response is untouched.
        var context = new DefaultHttpContext();
        await builder.Build()(context);
        Assert.Empty(context.Response.Headers);
    }

    [Fact]
    public async Task Sets_the_header_on_every_request()
    {
        var builder = new ApplicationBuilder(new ServiceCollection().BuildServiceProvider());
        builder.UseAspNetCoreSharedServer(Header);
        builder.Run(_ => Task.CompletedTask);
        var pipeline = builder.Build();

        var first = new DefaultHttpContext();
        var second = new DefaultHttpContext();
        await pipeline(first);
        await pipeline(second);

        Assert.Equal(Header, first.Response.Headers["Alt-Svc"].Single());
        Assert.Equal(Header, second.Response.Headers["Alt-Svc"].Single());
    }

    [Fact]
    public async Task Replaces_a_value_that_is_already_there_instead_of_adding_a_second_header()
    {
        var context = new DefaultHttpContext();
        context.Response.Headers["Alt-Svc"] = "h3=\":58705\""; // what Kestrel would advertise: its own internal port

        await RunAsync(app => app.UseAspNetCoreSharedServer(Header), context);

        Assert.Equal(new[] { Header }, context.Response.Headers["Alt-Svc"].ToArray());
    }

    [Fact]
    public async Task Reads_the_value_from_the_environment_variable_the_server_sets()
    {
        var previous = Environment.GetEnvironmentVariable(AspNetCoreSharedServerExtensions.AltSvcVariable);
        try
        {
            Environment.SetEnvironmentVariable(AspNetCoreSharedServerExtensions.AltSvcVariable, Header);
            var withValue = await RunAsync(app => app.UseAspNetCoreSharedServer());
            Environment.SetEnvironmentVariable(AspNetCoreSharedServerExtensions.AltSvcVariable, "");
            var withoutValue = await RunAsync(app => app.UseAspNetCoreSharedServer());

            Assert.Equal(Header, withValue.Response.Headers["Alt-Svc"].Single());
            Assert.False(withoutValue.Response.Headers.ContainsKey("Alt-Svc"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(AspNetCoreSharedServerExtensions.AltSvcVariable, previous);
        }
    }

    [Fact]
    public void Uses_the_same_variable_name_as_the_server()
    {
        // The middleware package does not reference the server, so the two names are kept in step by this test.
        Assert.Equal(Server.AltSvcVariable, AspNetCoreSharedServerExtensions.AltSvcVariable);
    }

    [Fact]
    public void Rejects_a_missing_application_builder()
    {
        Assert.Throws<ArgumentNullException>(() => AspNetCoreSharedServerExtensions.UseAspNetCoreSharedServer((IApplicationBuilder)null!));
    }
}
