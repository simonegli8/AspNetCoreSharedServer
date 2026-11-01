using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Xunit;

namespace AspNetCoreSharedServer.Tests;

/// <summary>
/// <c>aspnet-server stop &lt;app&gt;</c> against a real server: the app must be offline, reported as Stopped, and no longer listening, and
/// <c>start</c> must bring it back. Like the other end-to-end tests it only runs when asked (<c>ASPNETSERVER_E2E=1</c>), on Windows, and
/// elevated, because the server only loads a configuration.json that administrators alone can change. It needs neither QUIC nor a
/// certificate: the app listens on plain HTTP.
/// </summary>
[Collection("Server process")] // the server allows one instance per machine, so these tests run one after the other
[Trait("Category", "EndToEnd")]
public class AppPoolStopTests
{
    static string RunCli(string folder, params string[] arguments)
    {
        var info = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add(TestPaths.ServerDll);
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        info.Environment[Configuration.ConfigDirectoryVariable] = folder;
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(30000);
        return output.Trim();
    }

    static bool Listening(int port)
    {
        try
        {
            using var client = new TcpClient();
            return client.ConnectAsync(IPAddress.Loopback, port).Wait(2000) && client.Connected;
        }
        catch (Exception) { return false; }
    }

    static async Task<bool> WaitAsync(Func<bool> condition)
    {
        for (var i = 0; i < 60; i++)
        {
            if (condition()) return true;
            await Task.Delay(500);
        }
        return condition();
    }

    [SkippableFact]
    public async Task A_stopped_app_pool_is_offline_stopped_and_no_longer_listening_and_starts_again()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("ASPNETSERVER_E2E") == "1", "Opt-in: set ASPNETSERVER_E2E=1 to run the end-to-end tests.");
        Skip.IfNot(OperatingSystem.IsWindows(), "Only on Windows can the test tell the server where its configuration is (ASPNETSERVER_CONFIG_DIR).");
        Skip.IfNot(WindowsFileSecurity.IsAdministrator(), "The server only loads a configuration.json that administrators alone can change.");
        Skip.IfNot(File.Exists(TestPaths.ServerDll) && File.Exists(TestPaths.TestAppDll), "The server or the test app is not built.");

        var port = await Configuration.FindFreePortAsync(temporary: true);
        var folder = Directory.CreateTempSubdirectory("aspnet-server-stop-").FullName;
        var file = Path.Combine(folder, "configuration.json");
        File.WriteAllText(file, JsonSerializer.Serialize(new
        {
            User = Environment.UserName,
            MemoryLowThreshold = 1.01,
            Applications = new[]
            {
                new { Name = "TestApp", ListenUrls = $"http://localhost:{port}/", Urls = $"http://localhost:{port}", Assembly = TestPaths.TestAppDll },
            },
        }));
        WindowsFileSecurity.Protect(file, null);

        var info = new ProcessStartInfo("dotnet") { WorkingDirectory = folder, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add(TestPaths.ServerDll);
        info.Environment[Configuration.ConfigDirectoryVariable] = folder;
        using var server = Process.Start(info)!;
        server.BeginOutputReadLine();
        server.BeginErrorReadLine();
        try
        {
            Assert.True(await WaitAsync(() => RunCli(folder, "status", "TestApp").Contains("'Running'")), "The app pool did not start listening.");
            Assert.True(Listening(port));

            Assert.Contains("Stopped TestApp", RunCli(folder, "stop", "TestApp"));

            Assert.True(await WaitAsync(() => RunCli(folder, "status", "TestApp").Contains("'Stopped'")),
                "After stop the app pool should be reported as Stopped: " + RunCli(folder, "status", "TestApp"));
            Assert.Contains("offline", RunCli(folder, "status", "TestApp"));
            Assert.True(await WaitAsync(() => !Listening(port)), "A stopped app pool must not accept connections (the next one would start the app again).");

            Assert.Contains("Started TestApp", RunCli(folder, "start", "TestApp"));

            Assert.True(await WaitAsync(() => RunCli(folder, "status", "TestApp").Contains("'Running'")), "The app pool did not run again after start.");
            Assert.Contains("online", RunCli(folder, "status", "TestApp"));
            Assert.True(await WaitAsync(() => Listening(port)));
        }
        finally
        {
            if (!server.HasExited) server.Kill(true);
            server.WaitForExit(10000);
            try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
