using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Xunit;

namespace AspNetCoreSharedServer.Tests;

/// <summary>
/// Stopping the server (Ctrl+C, SIGINT) against a real server with real apps that it has started: the apps have to be shut down with the
/// server, and not be left behind for the operating system to deal with, and the configuration.json has to say what became of them: an
/// app that was running is stopped, and an app that had failed keeps its error (and its message), because that is what the next start
/// and <c>aspnet-server status</c> have to tell. <c>ShutdownAsync</c> used to look for the proxies in the applications of a configuration
/// that it had just loaded from the file, which never have one, so it shut nothing down; and what it wrote into the file was that loaded
/// configuration. Like the other end-to-end tests they only run when asked (<c>ASPNETSERVER_E2E=1</c>), and on Windows elevated, because the
/// server only loads a configuration.json that administrators alone can change. The apps listen on plain HTTP.
/// </summary>
[Collection("Server process")] // the tests of the real server run one after the other
[Trait("Category", "EndToEnd")]
public class ServerStopTests
{
    static bool Listening(int port)
    {
        try
        {
            using var client = new TcpClient();
            return client.ConnectAsync(IPAddress.Loopback, port).Wait(2000) && client.Connected;
        }
        catch (Exception) { return false; }
    }

    static async Task<bool> WaitAsync(Func<bool> condition, int seconds)
    {
        for (var i = 0; i < seconds * 2; i++)
        {
            if (condition()) return true;
            await Task.Delay(500);
        }
        return condition();
    }

    static bool IsAlive(int pid)
    {
        try { return !Process.GetProcessById(pid).HasExited; }
        catch (ArgumentException) { return false; }
    }

    static void RequirePrerequisites()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("ASPNETSERVER_E2E") == "1", "Opt-in: set ASPNETSERVER_E2E=1 to run the end-to-end tests.");
        Skip.If(OperatingSystem.IsWindows() && !WindowsFileSecurity.IsAdministrator(), "The server only loads a configuration.json that administrators alone can change.");
        Skip.IfNot(File.Exists(TestPaths.ServerDll) && File.Exists(TestPaths.TestAppDll), "The server or the test app is not built.");
    }

    /// <summary>The real server, in a temporary configuration folder, with the applications that the test gives it.</summary>
    sealed class RunningServer : IDisposable
    {
        readonly StringBuilder log = new();
        public string Folder { get; }
        public string File { get; }
        public Process Process { get; }
        public string Log { get { lock (log) return log.ToString(); } }

        public RunningServer(object[] applications)
        {
            Folder = Directory.CreateTempSubdirectory("aspnet-server-stop-server-").FullName;
            File = Path.Combine(Folder, "configuration.json");
            System.IO.File.WriteAllText(File, JsonSerializer.Serialize(new { User = Environment.UserName, MemoryLowThreshold = 1.01, Applications = applications }));
            if (OperatingSystem.IsWindows()) WindowsFileSecurity.Protect(File, null);
            else System.IO.File.SetUnixFileMode(File, System.IO.UnixFileMode.UserRead | System.IO.UnixFileMode.UserWrite);

            var info = new ProcessStartInfo("dotnet") { WorkingDirectory = Folder, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            info.ArgumentList.Add(TestPaths.ServerDll);
            info.ArgumentList.Add(Configuration.ConfigDirectoryOption);
            info.ArgumentList.Add(Folder);
            Process = Process.Start(info)!;
            Process.OutputDataReceived += (_, e) => { lock (log) log.AppendLine(e.Data); };
            Process.ErrorDataReceived += (_, e) => { lock (log) log.AppendLine(e.Data); };
            Process.BeginOutputReadLine();
            Process.BeginErrorReadLine();
        }

        /// <summary>Ctrl+C, as an administrator or a service manager stops the server. Returns true if the server has stopped.</summary>
        public bool Stop()
        {
            if (OperatingSystem.IsWindows()) Assert.True(SignalSender.SendSigintWindows(Process.Id), "Ctrl+C could not be sent to the server.");
            else SignalSender.SendSigint(Process.Id);
            return Process.WaitForExit(90000);
        }

        /// <summary>The status and the error of an application in the configuration.json (Stopped is the default, which is not written).</summary>
        public (string Status, string? Error) AppInFile(string name)
        {
            using var document = JsonDocument.Parse(System.IO.File.ReadAllText(File));
            var app = document.RootElement.GetProperty("Applications").EnumerateArray().First(a => a.GetProperty("Name").GetString() == name);
            return (app.TryGetProperty("Status", out var status) ? status.GetString()! : "Stopped",
                app.TryGetProperty("Error", out var error) ? error.GetString() : null);
        }

        public bool FileSays(string name, string status)
        {
            try { return AppInFile(name).Status == status; }
            catch (Exception) { return false; } // the server is writing it
        }

        public void Dispose()
        {
            if (!Process.HasExited) Process.Kill(true);
            Process.WaitForExit(10000);
            Process.Dispose();
            try { Directory.Delete(Folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    static async Task<int> AskForPidAsync(int port)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        string? answer = null;
        Assert.True(await WaitAsync(() =>
        {
            try { answer = client.GetStringAsync($"http://localhost:{port}/pid").GetAwaiter().GetResult(); return true; }
            catch (Exception) { return false; }
        }, 60), "The app did not answer.");
        return int.Parse(answer!);
    }

    [SkippableFact]
    public async Task The_app_that_the_server_started_is_shut_down_when_the_server_is_stopped()
    {
        RequirePrerequisites();
        var port = await Configuration.FindFreePortAsync(temporary: true);
        using var server = new RunningServer(new object[]
        {
            new { Name = "TestApp", ListenUrls = $"http://localhost:{port}/", Urls = $"http://localhost:{port}", Assembly = TestPaths.TestAppDll },
        });
        var appPid = 0;
        try
        {
            Assert.True(await WaitAsync(() => Listening(port), 40), "The server did not start listening.");

            // The first request starts the app, which says who it is.
            appPid = await AskForPidAsync(port);
            Assert.NotEqual(server.Process.Id, appPid);
            Assert.True(IsAlive(appPid), "The app should be running.");
            Assert.Equal("Running", server.AppInFile("TestApp").Status);

            Assert.True(server.Stop(), "The server did not stop.\n" + server.Log);
            Assert.True(await WaitAsync(() => !IsAlive(appPid), 30),
                $"The server has stopped, but the app (process {appPid}) that it started is still running.\n" + server.Log);

            // What the file says about the app now, for the next start of the server and for aspnet-server status
            Assert.Equal("Stopped", server.AppInFile("TestApp").Status);
        }
        finally
        {
            if (appPid != 0 && IsAlive(appPid))
            {
                try { Process.GetProcessById(appPid).Kill(true); } catch (Exception) { }
            }
        }
    }

    [SkippableFact]
    public async Task An_app_that_failed_to_start_keeps_its_error_when_the_server_is_stopped_and_the_others_are_stopped()
    {
        RequirePrerequisites();
        var goodPort = await Configuration.FindFreePortAsync(temporary: true);
        var brokenPort = await Configuration.FindFreePortAsync(temporary: true);
        using var server = new RunningServer(new object[]
        {
            new { Name = "TestApp", ListenUrls = $"http://localhost:{goodPort}/", Urls = $"http://localhost:{goodPort}", Assembly = TestPaths.TestAppDll },
            // an application whose assembly is not there fails to start, which puts it in Error and takes it offline
            new { Name = "Broken", ListenUrls = $"http://localhost:{brokenPort}/", Urls = $"http://localhost:{brokenPort}", Assembly = Path.Combine(Path.GetTempPath(), "no-such-app-folder", "NoSuchApp.dll") },
        });
        var appPid = 0;
        try
        {
            Assert.True(await WaitAsync(() => Listening(goodPort) && Listening(brokenPort), 40), "The server did not start listening.");
            appPid = await AskForPidAsync(goodPort);

            // A request for the broken app makes the server try to start it.
            using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) })
            {
                try { await client.GetAsync($"http://localhost:{brokenPort}/"); } catch (Exception) { } // there is nobody to answer
            }
            Assert.True(await WaitAsync(() => server.FileSays("Broken", "Error"), 30), "The broken app did not go into Error.\n" + server.Log);
            Assert.Equal("Assembly not found.", server.AppInFile("Broken").Error);
            Assert.Equal("Running", server.AppInFile("TestApp").Status);

            Assert.True(server.Stop(), "The server did not stop.\n" + server.Log);

            // The app that was running is stopped. The app that had failed still says so, with the reason: shutting down is not a fix.
            Assert.Equal("Stopped", server.AppInFile("TestApp").Status);
            var broken = server.AppInFile("Broken");
            Assert.Equal("Error", broken.Status);
            Assert.Equal("Assembly not found.", broken.Error);
        }
        finally
        {
            if (appPid != 0 && IsAlive(appPid))
            {
                try { Process.GetProcessById(appPid).Kill(true); } catch (Exception) { }
            }
        }
    }
}
