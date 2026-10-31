using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AspNetCoreSharedServer.Util;
using System.ServiceProcess;

namespace AspNetCoreSharedServer.Services
{
	public class WindowsServiceController : ServiceController
	{
        #region Windows Services Management
        public OSService[] GetOSServices()
        {
            try
            {
                List<OSService> winServices = new List<OSService>();

                System.ServiceProcess.ServiceController[] services = System.ServiceProcess.ServiceController.GetServices();
                foreach (var service in services)
                {
                    OSService winService = new OSService();
                    winService.Id = service.ServiceName;
                    winService.Name = service.DisplayName;
                    winService.Description = service.DisplayName;
                    winService.CanStop = service.CanStop;
                    winService.CanPauseAndContinue = service.CanPauseAndContinue;

                    OSServiceStatus status = OSServiceStatus.ContinuePending;
                    switch (service.Status)
                    {
                        case ServiceControllerStatus.ContinuePending: status = OSServiceStatus.ContinuePending; break;
                        case ServiceControllerStatus.Paused: status = OSServiceStatus.Paused; break;
                        case ServiceControllerStatus.PausePending: status = OSServiceStatus.PausePending; break;
                        case ServiceControllerStatus.Running: status = OSServiceStatus.Running; break;
                        case ServiceControllerStatus.StartPending: status = OSServiceStatus.StartPending; break;
                        case ServiceControllerStatus.Stopped: status = OSServiceStatus.Stopped; break;
                        case ServiceControllerStatus.StopPending: status = OSServiceStatus.StopPending; break;
                    }
                    winService.Status = status;

                    winServices.Add(winService);
                }

                return winServices.ToArray();
            }
            catch
            {
                throw;
            }
        }

        public void ChangeOSServiceStatus(string id, OSServiceStatus status)
        {
            try
            {
                // get all services
                System.ServiceProcess.ServiceController[] services = System.ServiceProcess.ServiceController.GetServices();

                // find required service
                foreach (var service in services)
                {
                    if (String.Compare(service.ServiceName, id, true) == 0)
                    {
                        if (status == OSServiceStatus.Paused
                            && service.Status == ServiceControllerStatus.Running)
                        {
                            service.Pause();
                            service.WaitForStatus(ServiceControllerStatus.Paused, TimeSpan.FromSeconds(60));
                        }
                        else if (status == OSServiceStatus.Running
                            && service.Status == ServiceControllerStatus.Stopped)
                        {
                            service.Start();
                            service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(60));
                        }
                        else if (status == OSServiceStatus.Stopped
                            && ((service.Status == ServiceControllerStatus.Running) ||
                                (service.Status == ServiceControllerStatus.Paused)))
                        {
                            service.Stop();
                            service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(60));
                        }
                        else if (status == OSServiceStatus.ContinuePending
                            && service.Status == ServiceControllerStatus.Paused)
                        {
                            service.Continue();
                            service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(60));
                        }
                    }
                }
            }
            catch
            {
                throw;
            }
        }
        #endregion

        public WindowsServiceController()
		{
			if (OSInfo.IsCore)
			{
				System.Reflection.Assembly.Load("System.ServiceProcess.ServiceController");
			}
		}
		public override bool IsInstalled => true;

		public override IEnumerable<OSService> All() => GetOSServices();

		public override void ChangeStatus(string serviceId, OSServiceStatus status)
		{
			ChangeOSServiceStatus(serviceId, status);
		}

		public override OSService Info(string serviceId)
			=> All().Where(s => s.Id.Equals(serviceId, StringComparison.OrdinalIgnoreCase)).FirstOrDefault();

		public override ServiceManager Install(ServiceDescription service)
		{
			var winService = service as WindowsServiceDescription;
			var cmd = $"sc.exe create {service.ServiceId} binPath= \"{service.Executable}\"";
			if (winService != null)
			{
				if (!string.IsNullOrEmpty(winService.DisplayName)) cmd += $" DisplayName= \"{winService.DisplayName}\"";
				if (winService.DependsOn != null && winService.DependsOn.Any())
				{
					cmd += $" depend= \"{string.Join("/", winService.DependsOn.Select(dep => dep.Trim()))}";
				}
				if (!string.IsNullOrEmpty(winService.Object))
				{
					cmd += $" obj= \"{winService.Object}\"";
				}
				if (winService.Type != WindowsServiceType.Own) cmd += $" type= {winService.Type.ToString().ToLower()}";
				if (winService.Error != WindowsServiceErrorHandling.Normal) cmd += $" error= {winService.Error.ToString().ToLower()}";
				if (winService.Start != WindowsServiceStartMode.Demand) cmd += $" start= {winService.Start.ToString().ToLower().Replace("delayedauto", "delayed-auto")}";
				if (winService.Tag.HasValue) cmd += $" tag= {(winService.Tag.Value ? "yes" : "no")}";
				if (!string.IsNullOrEmpty(winService.Group)) cmd += $" group= \"{winService.Group}\"";
				if (!string.IsNullOrEmpty(winService.Password)) cmd += $" password= \"{winService.Password}\"";
			}
			Shell.Standard.Exec(cmd);
			if (!string.IsNullOrEmpty(winService?.Description))
			{
				Shell.Standard.Exec($"sc.exe description {service.ServiceId} \"{winService.Description}\"");
            }

            return new ServiceManager(this, service.ServiceId);
		}

		public override void Remove(string serviceId)
		{
			Shell.Standard.Exec($"sc.exe delete {serviceId}");
		}

		public override void SystemReboot() => Shell.Standard.Exec("shutdown.exe -r -t 0");
	}
}
