using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using SabhaMujic.Server.Remote.Certificates;

namespace SabhaMujic.Service.Security;

/// <summary>
/// Protects the remote's private keys (local CA and TLS leaf) with DPAPI in <b>machine</b> scope plus fixed
/// application entropy, so only code running on this PC can decrypt them; the certs folder is additionally ACL'd to
/// SYSTEM, Administrators and the service. This calls <c>CryptProtectData</c>/<c>CryptUnprotectData</c> directly (what
/// <c>ProtectedData</c> does) instead of pulling the Windows Desktop framework into the service just for that class.
/// </summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    private const int CryptProtectUiForbidden = 0x1;
    private const int CryptProtectLocalMachine = 0x4;

    private static readonly byte[] DefaultEntropy = Encoding.UTF8.GetBytes("SabhaMujic.Remote.Certificates.v1");

    private readonly byte[] _entropy;

    public DpapiSecretProtector(byte[]? entropy = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("DPAPI is only available on Windows.");
        }

        _entropy = entropy is { Length: > 0 } ? entropy.ToArray() : DefaultEntropy;
    }

    public byte[] Protect(byte[] plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        return Transform(plaintext, protect: true);
    }

    public byte[] Unprotect(byte[] protectedData)
    {
        ArgumentNullException.ThrowIfNull(protectedData);
        return Transform(protectedData, protect: false);
    }

    private byte[] Transform(byte[] input, bool protect)
    {
        var inputHandle = GCHandle.Alloc(input, GCHandleType.Pinned);
        var entropyHandle = GCHandle.Alloc(_entropy, GCHandleType.Pinned);
        var output = default(DataBlob);
        try
        {
            var inBlob = new DataBlob { Size = input.Length, Data = inputHandle.AddrOfPinnedObject() };
            var entropyBlob = new DataBlob { Size = _entropy.Length, Data = entropyHandle.AddrOfPinnedObject() };
            const int flags = CryptProtectUiForbidden | CryptProtectLocalMachine;
            var ok = protect
                ? CryptProtectData(ref inBlob, null, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, flags, out output)
                : CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, flags, out output);
            if (!ok)
            {
                throw new CryptographicException(protect ? "DPAPI could not protect the data." : "DPAPI could not unprotect the data.",
                    new Win32Exception(Marshal.GetLastPInvokeError()));
            }

            var result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, output.Size);
            return result;
        }
        finally
        {
            if (output.Data != IntPtr.Zero)
            {
                if (!protect)
                {
                    // Plaintext key material: wipe the native copy before freeing it.
                    Marshal.Copy(new byte[output.Size], 0, output.Data, output.Size);
                }

                LocalFree(output.Data);
            }

            inputHandle.Free();
            entropyHandle.Free();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob dataIn, string? description, ref DataBlob entropy, IntPtr reserved,
        IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, ref DataBlob entropy, IntPtr reserved,
        IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
