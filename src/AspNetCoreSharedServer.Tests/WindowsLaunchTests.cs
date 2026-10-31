using System.Diagnostics;
using Xunit;

namespace AspNetCoreSharedServer.Tests;

/// <summary>
/// <b>Needs LocalSystem.</b> The real S4U launch: creates a local user, starts a process as it and checks from the inside that it
/// is that user and that it got the variables of the server. Only an account with the privilege "Act as part of the operating
/// system" can do that, and creating the account changes the machine, so the test only runs when asked and when it can:
/// <code>set ASPNETSERVER_S4U=1</code> and run the tests as LocalSystem (for example from a scheduled task that runs as SYSTEM).
/// The account is removed again at the end.
/// </summary>
public class WindowsLaunchTests
{
    [SkippableFact]
    [Trait("Category", "Windows-S4U")]
    public async Task An_app_runs_as_the_local_user_with_the_variables_of_the_server()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows only.");
        Skip.If(Environment.GetEnvironmentVariable("ASPNETSERVER_S4U") != "1", "Opt-in: set ASPNETSERVER_S4U=1 and run as LocalSystem.");
        var missing = WindowsLaunch.MissingPrivileges();
        Skip.If(missing != null, missing ?? "");

        var user = "aspsrv" + Guid.NewGuid().ToString("N")[..8];
        Assert.True(WindowsAccounts.EnsureUser(user));
        try
        {
            Assert.True(WindowsAccounts.UserExists(user));
            Assert.False(WindowsAccounts.EnsureUser(user)); // a second call finds it

            var output = new List<string>();
            var error = new List<string>();
            var done = new TaskCompletionSource();
            var environment = new Dictionary<string, string> { ["ASPNET_SERVER"] = "9.9.9", ["PROXY_PROTOCOL"] = "v2" };

            using var launch = WindowsLaunch.Start(user, "cmd.exe /c \"whoami & echo ASPNET_SERVER=%ASPNET_SERVER% & echo PROXY_PROTOCOL=%PROXY_PROTOCOL% & echo USERNAME=%USERNAME%\"",
                Path.GetTempPath(), environment,
                line => { if (line == null) done.TrySetResult(); else lock (output) output.Add(line); },
                line => { if (line != null) lock (error) error.Add(line); });
            launch.Process.EnableRaisingEvents = true;
            launch.Resume();

            await done.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(launch.Process.WaitForExit(30000));

            Assert.Empty(error);
            output = output.Select(line => line.TrimEnd()).ToList(); // cmd keeps the space in front of an '&'
            Assert.Contains(output, line => line.EndsWith("\\" + user, StringComparison.OrdinalIgnoreCase)); // whoami: MACHINE\user
            Assert.Contains("ASPNET_SERVER=9.9.9", output);
            Assert.Contains("PROXY_PROTOCOL=v2", output);
            Assert.Contains("USERNAME=" + user, output, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            WindowsAccounts.DeleteUser(user);
        }
    }

    [SkippableFact]
    [Trait("Category", "Windows-S4U")]
    public void Ending_the_launch_ends_the_app()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows only.");
        Skip.If(Environment.GetEnvironmentVariable("ASPNETSERVER_S4U") != "1", "Opt-in: set ASPNETSERVER_S4U=1 and run as LocalSystem.");
        var missing = WindowsLaunch.MissingPrivileges();
        Skip.If(missing != null, missing ?? "");

        var user = "aspsrv" + Guid.NewGuid().ToString("N")[..8];
        WindowsAccounts.EnsureUser(user);
        try
        {
            var launch = WindowsLaunch.Start(user, "ping.exe -n 60 127.0.0.1", Path.GetTempPath(), new Dictionary<string, string>(), _ => { }, _ => { });
            launch.Resume();
            var process = launch.Process;
            Assert.False(process.HasExited);

            launch.Dispose(); // closing the job kills what runs in it

            Assert.True(process.WaitForExit(10000));
        }
        finally
        {
            WindowsAccounts.DeleteUser(user);
        }
    }

    [SkippableFact]
    [Trait("Category", "Windows-S4U")]
    public async Task An_app_of_another_user_shuts_down_by_itself_when_the_server_sends_Ctrl_C()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows only.");
        Skip.If(Environment.GetEnvironmentVariable("ASPNETSERVER_S4U") != "1", "Opt-in: set ASPNETSERVER_S4U=1 and run as LocalSystem.");
        var missing = WindowsLaunch.MissingPrivileges();
        Skip.If(missing != null, missing ?? "");

        var user = "aspsrv" + Guid.NewGuid().ToString("N")[..8];
        WindowsAccounts.EnsureUser(user);
        try
        {
            var output = new List<string>();
            var ready = new TaskCompletionSource();
            var dotnet = Path.Combine(Environment.GetEnvironmentVariable("ProgramFiles") ?? @"C:\Program Files", "dotnet", "dotnet.exe");
            WindowsLaunch launch = null!;
            SignalSender.StartWithCtrlCEnabled(() => launch = WindowsLaunch.Start(user,
                $"\"{dotnet}\" \"{TestPaths.TestAssembly}\" --wait-for-sigint", Path.GetTempPath(), new Dictionary<string, string>(),
                line => { if (line == "ready") ready.TrySetResult(); if (line != null) lock (output) output.Add(line); }, _ => { }));
            using (launch)
            {
                launch.Resume();
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(30));

                // The same call the server makes to shut an app down: the app of another user has its own console.
                Assert.True(SignalSender.SendSigintWindows(launch.Process.Id));

                Assert.True(launch.Process.WaitForExit(15000), "The app of the other user did not shut down after Ctrl+C.");
                Assert.Equal(0, launch.ExitCode); // Process.ExitCode is not available for a process that another account started
                await Task.Delay(300); // the last line may still be on its way through the pipe
                lock (output) Assert.Contains("sigint", output);
            }
        }
        finally
        {
            WindowsAccounts.DeleteUser(user);
        }
    }
}
