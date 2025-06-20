using Microsoft.AspNetCore.Server.Kestrel.Core;
using System.Diagnostics;


namespace AspNetCoreTestApp;

public class Program
{
	public static void Main(string[] args)
	{
		var builder = WebApplication.CreateBuilder(args);

		// Add services to the container.
		builder.Services.AddRazorPages();


		var urls = builder.Configuration["URLS"]?.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
		int? httpPort = urls.Select(url => new Uri(url))
			.FirstOrDefault(uri => uri.Scheme == "http")
			?.Port;
		int? httpsPort = urls.Select(url => new Uri(url))
			.FirstOrDefault(uri => uri.Scheme == "https")
			?.Port;
	
		builder.WebHost.UseKestrel(options =>
		{
			if (httpPort.HasValue) options.ListenAnyIP(httpPort.Value); // Listen on port 5000
			if (httpsPort.HasValue) options.ListenAnyIP(httpsPort.Value, listenOptions =>
			{
				listenOptions.UseHttps(); // Enable HTTPS on port 5001
				listenOptions.Protocols = HttpProtocols.Http1AndHttp2AndHttp3;
			});
		});
		var app = builder.Build();

		// Configure the HTTP request pipeline.
		if (!app.Environment.IsDevelopment())
		{
			app.UseExceptionHandler("/Error");
			// The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
			app.UseHsts();
		}

		app.UseHttpsRedirection();
		app.UseStaticFiles();

		app.UseRouting();

		app.UseAuthorization();

		app.MapRazorPages();

		app.Run();
	}
}
