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
            lifetime.ApplicationStopping.Register(async () =>
            {
                //Logger.LogInformation("Application is stopping...");
                await Configuration.Current.ShutdownAsync();
			});
		}

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Configuration.Current.Logger = Logger;
            await Configuration.Current.WatchAsync();

            var tcs = new TaskCompletionSource();
            stoppingToken.Register(() => tcs.TrySetResult());

            await tcs.Task;
            //Logger.LogInformation("End of Worker");
        }
    }
}
