using System.Diagnostics;
namespace AspNetCoreSharedServer.Tests;

/// <summary>
/// The test assembly doubles as a tiny helper program. Tests start it as a child process (<c>dotnet AspNetCoreSharedServer.Tests.dll</c>)
/// to see exactly how command line arguments and environment variables arrive in another process.
/// </summary>
internal static class TestProgram
{
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--echo")
        {
            // One line per argument, in brackets.
            foreach (var arg in args.Skip(1)) Console.WriteLine("[" + arg + "]");
            return 0;
        }
        if (args.Length > 1 && args[0] == "--env")
        {
            Console.WriteLine(Environment.GetEnvironmentVariable(args[1]));
            return 0;
        }
        if (args.Length > 0 && args[0] == "--wait-for-sigint")
        {
            // Prints "ready", then waits for Ctrl+C: the way Kestrel is asked to shut down on Windows.
            var received = new ManualResetEventSlim();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                received.Set();
            };
            Console.WriteLine("ready");
            Console.Out.Flush();
            if (!received.Wait(TimeSpan.FromSeconds(30))) return 2;
            Console.WriteLine("sigint");
            return 0;
        }
        if (args.Length > 1 && args[0] == "--signal-then-print")
        {
            // What the server does at a shutdown: send Ctrl+C to an app, then keep logging to its own console.
            var sent = SignalSender.SendSigintWindows(int.Parse(args[1]));
            Console.WriteLine("sent=" + sent);
            Console.WriteLine("after-out");
            Console.Error.WriteLine("after-err");
            return 0;
        }
        if (args.Length > 1 && args[0] == "--two-apps")
        {
            // A server that runs from a console and starts two apps, then asks the first one to shut down. args[1]: CreateNoWindow.
            var createNoWindow = bool.Parse(args[1]);
            Process Start()
            {
                var info = new System.Diagnostics.ProcessStartInfo("dotnet", $"\"{typeof(TestProgram).Assembly.Location}\" --wait-for-sigint")
                {
                    RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = createNoWindow,
                };
                Process process = null!;
                SignalSender.StartWithCtrlCEnabled(() => process = Process.Start(info)!);
                process.StandardOutput.ReadLine(); // "ready"
                return process;
            }
            using var first = Start();
            using var second = Start();
            SignalSender.SendSigintWindows(first.Id);
            first.WaitForExit(10000);
            Thread.Sleep(1000);
            Console.WriteLine("first-exited=" + first.HasExited);
            Console.WriteLine("second-exited=" + second.HasExited);
            if (!second.HasExited) second.Kill(true);
            return 0;
        }
        return 1;
    }
}
