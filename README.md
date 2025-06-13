# AspNetCoreSharedServer
A ASP.NET Core server that starts Kestrel on demand and stops it when idle. For use when hosting ASP.NET Core in a shared hosting environment behind a proxy like Apache or Nginx, where Kestrel should not run as a permanent Systemd service using resources, but should be started on demand and shutdown when idle. 
