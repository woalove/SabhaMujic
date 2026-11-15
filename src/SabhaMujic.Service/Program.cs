// M0 skeleton: the real host (pipe server, Kestrel, engine) is built in M3.
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = "SabhaMujic");
builder.Build().Run();
