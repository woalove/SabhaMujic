using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace SabhaMujic.Windows.Tests;

[SupportedOSPlatform("windows")]
internal static class WindowsTestSupport
{
    public static bool IsElevatedAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>A fresh folder under %ProgramData% (which standard users can read by default), deleted on dispose.</summary>
    public static TempProgramDataDir NewProgramDataDir() => new();

    public static string RunNet(params string[] args)
    {
        var start = new ProcessStartInfo("net.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true, // never wait on a prompt
            UseShellExecute = false,
        };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit(30_000);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"net {args[0]} {args.ElementAtOrDefault(1)} failed ({process.ExitCode}): {output}");
        }

        return output;
    }

    private const int Logon32LogonInteractive = 2;
    private const int Logon32LogonNetwork = 3;
    private const int Logon32ProviderDefault = 0;

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LogonUserW(string userName, string domain, string password, int logonType, int logonProvider, out SafeAccessTokenHandle token);

    /// <summary>Logs the user on (interactive, falling back to network logon) and returns its token.</summary>
    public static SafeAccessTokenHandle LogOn(string userName, string password)
    {
        if (LogonUserW(userName, ".", password, Logon32LogonInteractive, Logon32ProviderDefault, out var token))
        {
            return token;
        }

        token.Dispose();
        if (LogonUserW(userName, ".", password, Logon32LogonNetwork, Logon32ProviderDefault, out token))
        {
            return token;
        }

        var error = Marshal.GetLastPInvokeError();
        token.Dispose();
        throw new InvalidOperationException($"LogonUser failed with error {error}.");
    }

    /// <summary>A temporary local standard (non-admin) user; deleted on dispose.</summary>
    public sealed class TempLocalUser : IDisposable
    {
        public TempLocalUser()
        {
            Name = "smtest" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
            // Exactly 14 characters (longer makes "net user" ask a question) with upper, lower, digit and symbol.
            Password = "Sm!" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)) + "a9z";
            RunNet("user", Name, Password, "/add", "/passwordchg:no", "/expires:never");
        }

        public string Name { get; }

        public string Password { get; }

        public void Dispose()
        {
            try
            {
                RunNet("user", Name, "/delete");
            }
            catch (InvalidOperationException)
            {
                // Best effort clean-up.
            }
        }
    }

    public sealed class TempProgramDataDir : IDisposable
    {
        public TempProgramDataDir()
        {
            Path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "SabhaMujicTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
