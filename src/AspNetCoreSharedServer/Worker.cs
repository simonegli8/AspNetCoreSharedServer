using AspNetCoreSharedServer.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using System.Reflection;

namespace AspNetCoreSharedServer
{
    public class Worker : BackgroundService
    {
		public readonly ILogger<Worker> Logger;
        public readonly IHostApplicationLifetime Lifetime;

        public Worker(ILogger<Worker> logger, IHostApplicationLifetime lifetime)
        {
            Configuration.Current.Logger = Logger = logger;
            Lifetime = lifetime;
            lifetime.ApplicationStopping.Register(() =>
            {
                //Logger.LogInformation("Application is stopping...");
                Configuration.Current.Shutdown();
			});
		}

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var args = Environment.GetCommandLineArgs();

            Configuration.Current.Logger = Logger;
            Configuration.Current.Watch();

            var tcs = new TaskCompletionSource();
            stoppingToken.Register(() => tcs.TrySetResult());

            await tcs.Task;
            Logger.LogInformation("End of Worker");
        }
    }
}
