using System.Reflection;

namespace AspNetCoreSharedServer
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var assembly = Assembly.GetExecutingAssembly();
            Console.WriteLine($"AspNetCoreSharedServer {assembly.GetName().Version}");

            var builder = Host.CreateApplicationBuilder(args);
            builder.Services.AddHostedService<Worker>();

            var host = builder.Build();
            host.Run();
        }
    }
}