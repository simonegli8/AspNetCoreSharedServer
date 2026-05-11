using System.Reflection;

namespace AspNetCoreSharedServer;

public class Program
{
    public static void Main(string[] args)
    {
        var assembly = Assembly.GetExecutingAssembly();
        Console.WriteLine($"AspNetCoreSharedServer {assembly.GetName().Version}");

        if (args.Contains("-V")) return;

        var builder = Host.CreateApplicationBuilder(args);
        builder.Services.AddHostedService<Worker>();

        try
        {
            var host = builder.Build();
            host.Run();
            Console.WriteLine("End of Run");
        } catch (Exception ex)
        {
            Console.WriteLine($"Unahndled exception {ex}");
        }

    }
}