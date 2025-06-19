namespace AspNetCoreSharedServer
{
    public class Worker : BackgroundService
    {
        public CancellationToken Cancel = new CancellationToken();

		public readonly ILogger<Worker> Logger;

        public Worker(ILogger<Worker> logger)
        {
            Logger = logger;
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
