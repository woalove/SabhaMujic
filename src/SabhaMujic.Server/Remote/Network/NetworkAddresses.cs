using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace SabhaMujic.Server.Remote.Network;

/// <summary>The machine's LAN identity as seen by phones: its private IPv4 addresses and mDNS host name.</summary>
public interface INetworkAddressProvider
{
    /// <summary>Every current non-loopback private (RFC 1918) IPv4 address, distinct and sorted.</summary>
    IReadOnlyList<IPAddress> GetPrivateIPv4Addresses();

    /// <summary>The address to put in QR codes: the default-route interface's private IPv4 first. Null when there is none.</summary>
    IPAddress? GetPrimaryIPv4();

    /// <summary>The host's DNS label (lowercase, mDNS-safe), without ".local".</summary>
    string GetHostName();
}

/// <summary>Address classification for the remote (which peers may connect, which addresses go into the certificate).</summary>
public static class AddressRules
{
    /// <summary>RFC 1918: 10/8, 172.16/12, 192.168/16.</summary>
    public static bool IsPrivateIPv4(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        Span<byte> b = stackalloc byte[4];
        address.TryWriteBytes(b, out _);
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);
    }

    /// <summary>
    /// Peers allowed to reach the remote at all: loopback, private RFC 1918 and link-local IPv4, IPv6 unique-local
    /// (fc00::/7) and link-local (fe80::/10). Everything else (public addresses) is refused.
    /// </summary>
    public static bool IsAllowedPeer(IPAddress? address)
    {
        if (address is null)
        {
            return false;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            Span<byte> b = stackalloc byte[4];
            address.TryWriteBytes(b, out _);
            return IsPrivateIPv4(address) || (b[0] == 169 && b[1] == 254);
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            Span<byte> b = stackalloc byte[16];
            address.TryWriteBytes(b, out _);
            var uniqueLocal = (b[0] & 0xFE) == 0xFC;
            var linkLocal = b[0] == 0xFE && (b[1] & 0xC0) == 0x80;
            return uniqueLocal || linkLocal;
        }

        return false;
    }

    /// <summary>Reduces a host name to an mDNS-safe lowercase DNS label ("SABHA-PC_1" → "sabha-pc-1").</summary>
    public static string ToDnsLabel(string hostName)
    {
        var first = (hostName ?? "").Split('.', 2)[0];
        var sb = new StringBuilder(first.Length);
        foreach (var c in first.ToLowerInvariant())
        {
            sb.Append(c is >= 'a' and <= 'z' or >= '0' and <= '9' ? c : '-');
        }

        var label = sb.ToString().Trim('-');
        if (label.Length > 63)
        {
            label = label[..63].TrimEnd('-');
        }

        return label.Length == 0 ? "sabhamujic" : label;
    }
}

/// <summary>Reads the addresses from the operating system's network interfaces.</summary>
public sealed class SystemNetworkAddressProvider : INetworkAddressProvider
{
    public IReadOnlyList<IPAddress> GetPrivateIPv4Addresses() =>
        Candidates().Select(c => c.Address).Distinct().OrderBy(a => a.ToString(), StringComparer.Ordinal).ToList();

    public IPAddress? GetPrimaryIPv4()
    {
        var candidates = Candidates();
        if (candidates.Count == 0)
        {
            return null;
        }

        // 1. The source address the OS would use for the default route (no packet is sent by a UDP "connect").
        if (DefaultRouteSource() is { } source && candidates.Any(c => c.Address.Equals(source)))
        {
            return source;
        }

        // 2. An interface that has a gateway, preferring Wi-Fi/Ethernet; 3. any private address.
        return candidates
            .OrderByDescending(c => c.HasGateway)
            .ThenBy(c => c.Kind switch
            {
                NetworkInterfaceType.Wireless80211 => 0,
                NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet => 1,
                _ => 2,
            })
            .ThenBy(c => c.Address.ToString(), StringComparer.Ordinal)
            .First().Address;
    }

    public string GetHostName()
    {
        try
        {
            return AddressRules.ToDnsLabel(Dns.GetHostName());
        }
        catch (SocketException)
        {
            return AddressRules.ToDnsLabel(Environment.MachineName);
        }
    }

    private static IPAddress? DefaultRouteSource()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(new IPEndPoint(IPAddress.Parse("192.0.2.1"), 9)); // TEST-NET-1: never contacted
            return (socket.LocalEndPoint as IPEndPoint)?.Address;
        }
        catch (SocketException)
        {
            return null;
        }
    }

    private static List<(IPAddress Address, bool HasGateway, NetworkInterfaceType Kind)> Candidates()
    {
        var result = new List<(IPAddress, bool, NetworkInterfaceType)>();
        NetworkInterface[] interfaces;
        try
        {
            interfaces = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException)
        {
            return result;
        }

        foreach (var nic in interfaces)
        {
            try
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                var props = nic.GetIPProperties();
                var hasGateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                foreach (var unicast in props.UnicastAddresses)
                {
                    if (AddressRules.IsPrivateIPv4(unicast.Address) && !IPAddress.IsLoopback(unicast.Address))
                    {
                        result.Add((unicast.Address, hasGateway, nic.NetworkInterfaceType));
                    }
                }
            }
            catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException)
            {
            }
        }

        return result;
    }
}
