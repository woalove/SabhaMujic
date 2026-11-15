using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SabhaMujic.Server.Hosting;
using SabhaMujic.Service;
using SabhaMujic.Service.Logging;
using SabhaMujic.Service.Security;

// SabhaMujic Windows service (runs as NT SERVICE\SabhaMujic). Also runs as a console app for diagnostics:
//   SabhaMujic.Service.exe [--SabhaMujic:DataRoot=<dir>] [--SabhaMujic:PipeName=<name>]
// Data root default: %ProgramData%\SabhaMujic with data\, library\, uploads\ (ACL-protected) and logs\.

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = ServiceSid.ServiceName);
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(30));

var dataRoot = builder.Configuration["SabhaMujic:DataRoot"] ?? WindowsHostComposition.DefaultDataRoot;
var pipeName = builder.Configuration["SabhaMujic:PipeName"];
var paths = new SabhaDataPaths(Path.GetFullPath(dataRoot));

// Rolling file log in <root>\logs, next to the default console/debug/Event Log providers of the Windows host.
Directory.CreateDirectory(paths.Logs);
builder.Logging.AddProvider(new RollingFileLoggerProvider(paths.Logs));

builder.Services.AddSingleton(sp => WindowsHostComposition.CreateOptions(paths.Root, sp.GetRequiredService<ILoggerFactory>(), pipeName));
builder.Services.AddHostedService<SabhaServiceHost>();

await builder.Build().RunAsync();
