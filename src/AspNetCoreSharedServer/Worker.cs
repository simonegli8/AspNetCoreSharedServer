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
		}

        /// <summary>
        /// The host waits for this when it stops. The applications are shut down here, and not in a callback of ApplicationStopping:
        /// that callback runs on the thread that the console handler of Ctrl+C runs on, and shutting down an application there (on
        /// Windows it sends the application a Ctrl+C of its own, which means leaving and entering consoles) hung. An exception that
        /// nobody catches in an async callback ends the process (it did, with an ArrayTypeMismatchException, when the configuration
        /// could not be loaded), so a failure is logged here.
        /// </summary>
        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            try
            {
                await Configuration.Current.ShutdownAsync();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error while shutting down the applications.");
            }
            await base.StopAsync(cancellationToken);
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
