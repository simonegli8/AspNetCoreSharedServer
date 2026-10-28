using System.Reflection;

namespace AspNetCoreSharedServer.Tests;

/// <summary>Paths of the built server and test app, passed in by the project file as assembly metadata.</summary>
internal static class TestPaths
{
    public static string ServerDll => Metadata("ServerDll");
    public static string TestAppDll => Metadata("TestAppDll");
    /// <summary>The test assembly itself, which can be run as a helper program, see <see cref="TestProgram"/>.</summary>
    public static string TestAssembly => Assembly.GetExecutingAssembly().Location;

    static string Metadata(string key) =>
        Path.GetFullPath(Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
            .First(attribute => attribute.Key == key).Value!);
}
