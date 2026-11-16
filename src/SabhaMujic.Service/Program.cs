using SabhaMujic.Service;

// SabhaMujic Windows service (runs as NT SERVICE\SabhaMujic). Also runs as a console app for diagnostics:
//   SabhaMujic.Service.exe [--SabhaMujic:DataRoot=<dir>] [--SabhaMujic:PipeName=<name>]
//                          [--SabhaMujic:Remote:Enabled=true|false] [--SabhaMujic:Remote:HttpsPort=8443]
//                          [--SabhaMujic:Remote:BootstrapEnabled=true|false] [--SabhaMujic:Remote:BootstrapPort=8080]
//   SabhaMujic.Service.exe --prepare-data-root   (elevated: take ownership of and lock down the data root, then exit)
// Data root default: %ProgramData%\SabhaMujic with data\, library\, uploads\, certs\ (protected) and logs\ (Users read).
// All keys are described in ServiceSettings; startup steps in ServiceProgram.
return await ServiceProgram.RunAsync(args);
