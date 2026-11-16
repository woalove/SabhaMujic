using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SabhaMujic.Server.Hosting;
using SabhaMujic.Service;
using SabhaMujic.Service.Logging;
using SabhaMujic.Service.Security;

// SabhaMujic Windows service (runs as NT SERVICE\SabhaMujic). Also runs as a console app for diagnostics:
//   SabhaMujic.Service.exe [--SabhaMujic:DataRoot=<dir>] [--SabhaMujic:PipeName=<name>]
//                          [--SabhaMujic:Remote:Enabled=true|false] [--SabhaMujic:Remote:HttpsPort=8443]
//                          [--SabhaMujic:Remote:BootstrapEnabled=true|false] [--SabhaMujic:Remote:BootstrapPort=8080]
// Data root default: %ProgramData%\SabhaMujic with data\, library\, uploads\, certs\ (ACL-protected) and logs\.
// All keys are described in ServiceSettings.

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = ServiceSid.ServiceName);
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(30));

var settings = ServiceSettings.From(builder.Configuration);
var paths = new SabhaDataPaths(settings.DataRoot);

// Rolling file log in <root>\logs, next to the default console/debug/Event Log providers of the Windows host.
Directory.CreateDirectory(paths.Logs);
builder.Logging.AddProvider(new RollingFileLoggerProvider(paths.Logs));

builder.Services.AddSingleton(sp => WindowsHostComposition.CreateOptions(settings, sp.GetRequiredService<ILoggerFactory>()));
builder.Services.AddHostedService<SabhaServiceHost>();

await builder.Build().RunAsync();
