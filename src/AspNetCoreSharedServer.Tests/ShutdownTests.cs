using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AspNetCoreSharedServer.Tests;

/// <summary>
/// Shutting the server down when its configuration could not be loaded. That used to end the process: <c>ShutdownAsync</c> passed a LINQ
/// sequence of <c>Task&lt;Task&gt;</c> over an empty array (<c>Enumerable.Empty</c>, what it uses when there is no configuration) to
/// <c>Task.WaitAll</c>, which throws <see cref="ArrayTypeMismatchException"/>, and the callback that the worker registers for the
/// stopping of the application did not catch it, so the runtime aborted the process. The tests use a temporary configuration folder.
/// </summary>
[Collection("Working directory")] // the folder is a static setting of the process
public class ShutdownTests : IDisposable
{
    readonly string? previousFolder = Configuration.ConfigDirectoryOverride;
    readonly string folder = Path.Combine(Path.GetTempPath(), "aspsrv-shutdown-" + Guid.NewGuid().ToString("N"));

    public ShutdownTests()
    {
        Directory.CreateDirectory(folder);
        Configuration.ConfigDirectoryOverride = folder;
    }

    public void Dispose()
    {
        Configuration.ConfigDirectoryOverride = previousFolder;
        Configuration.Current.Logger = null;
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
    }

    string ConfigurationFile => Path.Combine(folder, "configuration.json");

    /// <summary>A configuration.json that cannot be parsed: loading it gives null, the case in which the shutdown crashed.</summary>
    void WriteUnloadableConfiguration() => File.WriteAllText(ConfigurationFile, "{ this is not json");

    /// <summary>
    /// A configuration.json that the server refuses because somebody else than the administrators and its own user can change it:
    /// loading it gives null as well, and the file is saved with the error in it.
    /// </summary>
    void WriteRefusedConfiguration()
    {
        File.WriteAllText(ConfigurationFile, "{ \"Applications\": [] }");
        if (OperatingSystem.IsWindows())
        {
            var security = new FileInfo(ConfigurationFile).GetAccessControl();
            security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.WorldSid, null),
                System.Security.AccessControl.FileSystemRights.Modify, System.Security.AccessControl.AccessControlType.Allow));
            new FileInfo(ConfigurationFile).SetAccessControl(security);
        }
        else File.SetUnixFileMode(ConfigurationFile, System.IO.UnixFileMode.UserRead | System.IO.UnixFileMode.UserWrite | System.IO.UnixFileMode.OtherWrite);
    }

    /// <summary>A configuration.json that the server accepts. On Windows that takes an administrator, who can protect the file.</summary>
    void WriteLoadableConfiguration()
    {
        File.WriteAllText(ConfigurationFile, "{ \"Applications\": [] }");
        if (OperatingSystem.IsWindows())
        {
            Skip.IfNot(WindowsFileSecurity.IsAdministrator(), "The server only loads a configuration.json that administrators alone can change.");
            WindowsFileSecurity.Protect(ConfigurationFile, null);
        }
        else File.SetUnixFileMode(ConfigurationFile, System.IO.UnixFileMode.UserRead | System.IO.UnixFileMode.UserWrite);
    }

    sealed class Lifetime : IHostApplicationLifetime
    {
        public readonly CancellationTokenSource Stopping = new();
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => Stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => Stopping.Cancel();
    }

    sealed class RecordingLogger : ILogger<Worker>
    {
        public readonly List<(LogLevel Level, string Message, Exception? Exception)> Entries = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Entries) Entries.Add((logLevel, formatter(state, exception), exception));
        }
    }

    [Fact]
    public async Task Shutting_down_without_a_loadable_configuration_does_not_throw()
    {
        WriteUnloadableConfiguration();

        await new Configuration().ShutdownAsync();
    }

    [Fact]
    public async Task Shutting_down_with_a_configuration_that_is_refused_does_not_throw()
    {
        WriteRefusedConfiguration();

        await new Configuration().ShutdownAsync();
    }

    [SkippableFact]
    public async Task Shutting_down_with_a_loadable_configuration_without_applications_does_not_throw()
    {
        WriteLoadableConfiguration();

        await new Configuration().ShutdownAsync();
    }

    [SkippableFact]
    public async Task Restarting_with_nothing_to_restart_does_not_throw()
    {
        WriteLoadableConfiguration();

        await new Configuration().RestartAsync();
    }

    [Fact]
    public async Task A_failure_while_shutting_down_is_logged_and_does_not_end_the_process()
    {
        // The configuration folder is a file: creating the lock file in it fails, and so does the shutdown.
        var notAFolder = Path.Combine(folder, "a-file");
        File.WriteAllText(notAFolder, "");
        Configuration.ConfigDirectoryOverride = notAFolder;
        var logger = new RecordingLogger();
        var worker = new Worker(logger, new Lifetime());

        // The host waits for StopAsync when it stops. An exception that it lets out is an unhandled exception of the stop.
        var exception = await Record.ExceptionAsync(() => worker.StopAsync(CancellationToken.None));

        Assert.Null(exception);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Error && entry.Exception != null);
    }
}
