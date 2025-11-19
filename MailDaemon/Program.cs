using MailDaemon;
using MailDaemon.Services;
using MailDaemon.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

// Registrar servicios
builder.Services.AddHostedService<DaemonWorker>();
builder.Services.Configure<MailSettings>(builder.Configuration.GetSection("MailSettings"));
builder.Services.Configure<DaemonSettings>(builder.Configuration.GetSection("Daemon"));
builder.Services.AddSingleton<DaemonService>();

var host = builder.Build();
host.Run();
