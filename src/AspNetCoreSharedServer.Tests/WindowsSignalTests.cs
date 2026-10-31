using System.Diagnostics;
using Xunit;

namespace AspNetCoreSharedServer.Tests;

/// <summary>
/// Shutting an app down on Windows: Kestrel is asked with Ctrl+C on the console of the process, there is no SIGINT. The helper program
/// (TestProgram --wait-for-sigint) waits for exactly that, the way a Kestrel app does.
/// </summary>
public class WindowsSignalTests
{
    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Ctrl_C_reaches_a_console_app_that_has_its_own_hidden_console_and_it_exits_by_itself(bool redirectInput)
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows only.");

        var info = new ProcessStartInfo("dotnet", $"\"{TestPaths.TestAssembly}\" --wait-for-sigint")
        {
            RedirectStandardOutput = true,
            RedirectStandardInput = redirectInput, // the server redirects it for an app
            UseShellExecute = false,
            CreateNoWindow = true, // the app gets a console that nobody sees
        };
        Process process = null!;
        SignalSender.StartWithCtrlCEnabled(() => process = Process.Start(info)!);
        using var _ = process;
        try
        {
            Assert.Equal("ready", process.StandardOutput.ReadLine());

            Assert.True(SignalSender.SendSigintWindows(process.Id));

            Assert.True(process.WaitForExit(15000), "The app did not shut down after Ctrl+C.");
            Assert.Equal("sigint", process.StandardOutput.ReadLine());
            Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            if (!process.HasExited) process.Kill(true);
        }
    }

    [SkippableFact]
    public void Ctrl_C_is_refused_for_a_process_that_does_not_exist()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows only.");

        Assert.False(SignalSender.SendSigintWindows(int.MaxValue));
    }

    [SkippableFact]
    public void The_server_keeps_working_after_it_has_sent_Ctrl_C()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows only.");

        // The server ignores the event for itself while it is attached to the console of the app; it must not stay deaf to Ctrl+C or lose
        // its console for good. Send twice to see that the second send still works.
        for (var i = 0; i < 2; i++)
        {
            var info = new ProcessStartInfo("dotnet", $"\"{TestPaths.TestAssembly}\" --wait-for-sigint")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            Process process = null!;
            SignalSender.StartWithCtrlCEnabled(() => process = Process.Start(info)!);
            using var _ = process;
            try
            {
                Assert.Equal("ready", process.StandardOutput.ReadLine());
                Assert.True(SignalSender.SendSigintWindows(process.Id));
                Assert.True(process.WaitForExit(15000));
            }
            finally
            {
                if (!process.HasExited) process.Kill(true);
            }
        }
    }

    [SkippableFact]
    public void The_server_can_still_write_to_its_own_output_after_it_has_sent_Ctrl_C()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows only.");

        // The server leaves its console to join the one of the app. What it logs afterwards must still arrive.
        var appInfo = new ProcessStartInfo("dotnet", $"\"{TestPaths.TestAssembly}\" --wait-for-sigint")
        {
            RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
        };
        Process app = null!;
        SignalSender.StartWithCtrlCEnabled(() => app = Process.Start(appInfo)!);
        using var _ = app;
        try
        {
            Assert.Equal("ready", app.StandardOutput.ReadLine());

            var (lines, server) = HelperProcess.Run($"--signal-then-print {app.Id}", info => info.RedirectStandardError = false);

            Assert.Equal(0, server.ExitCode);
            Assert.Equal(new[] { "sent=True", "after-out" }, lines);
            Assert.True(app.WaitForExit(15000));
        }
        finally
        {
            if (!app.HasExited) app.Kill(true);
        }
    }

    [SkippableTheory]
    [InlineData(true, false)]  // CreateNoWindow: every app has a console of its own, so Ctrl+C reaches only the one that is meant
    [InlineData(false, true)]  // no CreateNoWindow: the apps share the console of a server that runs in a console, and all of them get it
    public void Ctrl_C_for_one_app_reaches_the_other_apps_only_if_they_share_its_console(bool createNoWindow, bool secondAlsoExits)
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows only.");

        // The helper runs with a console of its own (hidden), like a server that was started from a console window.
        var (lines, server) = HelperProcess.Run($"--two-apps {createNoWindow}", info => info.CreateNoWindow = true);

        Assert.Equal(0, server.ExitCode);
        Assert.Equal("first-exited=True", lines[0]);
        Assert.Equal("second-exited=" + secondAlsoExits, lines[1]);
    }
}
