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
        return 1;
    }
}
