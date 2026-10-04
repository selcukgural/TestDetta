using System.Net;
using System.Net.Sockets;

namespace TestDetta.LicenseService;

/// <summary>The rate-limit partition of a client address.</summary>
public static class ClientPartition
{
    /// <summary>
    /// An IPv4 address as it is; an IPv6 address by its /64 network, which one customer typically holds
    /// whole, so rotating addresses inside it does not dodge the per-address limit.
    /// </summary>
    /// <param name="address">The client address; null when unknown.</param>
    public static string Of(IPAddress? address)
    {
        if (address is null)
        {
            return "unknown";
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }

        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes) + "/64";
    }
}
