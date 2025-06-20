namespace AspNetCoreSharedServer
{
    public class Worker : BackgroundService
    {
		public readonly ILogger<Worker> Logger;
        public readonly IHostApplicationLifetime Lifetime;

        public Worker(ILogger<Worker> logger, IHostApplicationLifetime lifetime)
        {
            Logger = logger;
            Lifetime = lifetime;
            lifetime.ApplicationStopping.Register(() =>
            {
                //Logger.LogInformation("Application is stopping...");
                Configuration.Current.IsShuttingDown = true;
				var apps = Configuration.Current.Applications.ToList();
                foreach (var app in apps)
                {
                    if (app.Server != null) app.Server.Shutdown();
				}
			});
		}

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Configuration.Current.Logger = Logger;
            Configuration.Current.Watch();

            /*while (!stoppingToken.IsCancellationRequested && !Cancel.IsCancellationRequested)
            {
                await Task.Delay(5000, stoppingToken);
            }

            Configuration.Current.Shutdown();*/
		}
    }
}
