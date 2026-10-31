using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AspNetCoreSharedServer.Tests;

/// <summary>
/// The real server (<c>dotnet AspNetCoreSharedServer.dll</c>) running in a temporary folder with its own configuration.json,
/// with the test app as its only application. On Windows the server reads its configuration from the current directory,
/// which is what makes this safe: the machine's real configuration is never touched.
/// </summary>
internal sealed class ServerHost : IAsyncDisposable
{
    readonly string directory;
    readonly Process process;
    readonly StringBuilder log = new();

    /// <summary>The public HTTPS (and UDP) port of the application.</summary>
    public int Port { get; }

    ServerHost(int port, string directory, Process process)
    {
        Port = port;
        this.directory = directory;
        this.process = process;
    }

    public string Log
    {
        get { lock (log) return log.ToString(); }
    }

    public int CountInLog(string text) => Regex.Count(Log, Regex.Escape(text));

    public async Task WaitForLogAsync(string text, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!Log.Contains(text))
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"'{text}' did not appear in the server log within {timeout}. Log:\n{Log}");
            await Task.Delay(100);
        }
    }

    public static async Task<ServerHost> StartAsync(string idleTimeout, string recycle)
    {
        var port = await Configuration.FindFreePortAsync(temporary: true, alsoUdp: true);
        var directory = Directory.CreateTempSubdirectory("aspnet-server-e2e-").FullName;
        var configuration = new
        {
            // The apps run as the account of the server, which starts them directly.
            User = Environment.UserName,
            Applications = new[]
            {
                new
                {
                    Name = "TestApp",
                    ListenUrls = $"https://localhost:{port}/",
                    Urls = $"https://localhost:{port}",
                    Assembly = TestPaths.TestAppDll,
                    IdleTimeout = idleTimeout,
                    Recycle = recycle,
                },
            },
            IdleTimeout = idleTimeout,
            Recycle = recycle,
            EnableHttp3 = true,
            // Above 100% used memory is never reached, so the machine never counts as low on memory. Under memory pressure the idle
            // timeout switches to IdleTimeoutOnLowMemory (1 minute by default), which would make the idle test depend on the machine.
            MemoryLowThreshold = 1.01,
        };
        var configurationFile = Path.Combine(directory, "configuration.json");
        File.WriteAllText(configurationFile, JsonSerializer.Serialize(configuration));
        // The server only loads a configuration.json that nobody but the administrators and SYSTEM can change (the user in the file is
        // not known yet when it is read), so on Windows it has to run elevated to read it.
        if (OperatingSystem.IsWindows()) WindowsFileSecurity.Protect(configurationFile, null);

        var info = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        info.ArgumentList.Add(TestPaths.ServerDll);
        // On Windows the server reads its configuration from %ProgramData%spnet-server, never from here, unless it is told where.
        info.Environment[Configuration.ConfigDirectoryVariable] = directory;
        var process = new Process { StartInfo = info };
        var host = new ServerHost(port, directory, process);
        process.OutputDataReceived += (_, e) => host.Append(e.Data);
        process.ErrorDataReceived += (_, e) => host.Append(e.Data);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await host.WaitForLogAsync("forwarding HTTP/3", TimeSpan.FromSeconds(30));
        }
        catch
        {
            await host.DisposeAsync();
            throw;
        }
        return host;
    }

    void Append(string? line)
    {
        if (line == null) return;
        lock (log) log.AppendLine(line);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
        catch (InvalidOperationException) { }
        process.Dispose();
        try { Directory.Delete(directory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
