using System.Diagnostics;

namespace AspNetCoreSharedServer.Tests;

/// <summary>Runs the test assembly as a helper program (see <see cref="TestProgram"/>) and returns what it printed, one entry per line.</summary>
internal static class HelperProcess
{
    public static (string[] Lines, Process Process) Run(string arguments, Action<ProcessStartInfo>? configure = null)
    {
        var info = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, UseShellExecute = false };
        info.Arguments = $"\"{TestPaths.TestAssembly}\" {arguments}";
        configure?.Invoke(info);
        var process = Process.Start(info)!;
        var lines = process.StandardOutput.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r')).ToArray();
        process.WaitForExit();
        return (lines, process);
    }

    /// <summary>
    /// How a process receives a command line: the arguments in front of and behind the first "--". It is the real argument parser of
    /// the runtime that splits them, the same one that parses the arguments of sudo when the server starts it.
    /// </summary>
    public static string[] Arguments(string commandLine) =>
        Run("--echo " + commandLine).Lines.Select(line => line[1..^1]).ToArray(); // each argument is printed as [argument]
}
