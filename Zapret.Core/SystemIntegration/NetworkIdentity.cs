using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;

namespace Zapret.Core.SystemIntegration;

public enum NetworkKind
{
    Unknown,
    Wired,
    WiFi,
    Mobile,
}

/// <summary>
/// Enough identity to remember a strategy per connection, and nothing more. The identifier is a truncated
/// hash of the adapter and gateway, so settings never carry a network name, a MAC address or an IP that
/// could identify where the user is (SPEC.md §13, §20).
/// </summary>
public sealed record NetworkProfile(string Id, NetworkKind Kind, string? AdapterName, string? Gateway)
{
    public static NetworkProfile Unknown { get; } = new("unknown", NetworkKind.Unknown, null, null);

    /// <summary>Localisation key for the connection kind; the service never produces display text.</summary>
    public string KindKey => Kind switch
    {
        NetworkKind.WiFi => "network.wifi",
        NetworkKind.Wired => "network.ethernet",
        NetworkKind.Mobile => "network.mobile",
        _ => "common.noData",
    };
}

public static class NetworkIdentity
{
    /// <summary>
    /// The connection actually carrying traffic: up, not loopback, and with a default gateway. Picking by
    /// gateway rather than by name avoids being fooled by virtual adapters from VPNs and hypervisors, which
    /// are usually up but have none.
    /// </summary>
    public static NetworkProfile Detect()
    {
        try
        {
            var candidates = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                            && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                            && n.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                .Select(n => (Adapter: n, Properties: n.GetIPProperties()))
                .Where(x => x.Properties.GatewayAddresses.Any(g => g.Address is not null))
                .ToList();

            if (candidates.Count == 0) return NetworkProfile.Unknown;

            // Prefer a physical connection over anything virtual that still reports a gateway.
            var chosen = candidates
                .OrderByDescending(x => Classify(x.Adapter.NetworkInterfaceType) != NetworkKind.Unknown)
                .ThenByDescending(x => x.Adapter.Speed)
                .First();

            var gateway = chosen.Properties.GatewayAddresses
                .FirstOrDefault(g => g.Address is not null)?.Address?.ToString();

            return new NetworkProfile(
                Fingerprint(chosen.Adapter.Id, gateway),
                Classify(chosen.Adapter.NetworkInterfaceType),
                chosen.Adapter.Name,
                gateway);
        }
        catch (NetworkInformationException)
        {
            return NetworkProfile.Unknown;
        }
    }

    private static NetworkKind Classify(NetworkInterfaceType type) => type switch
    {
        NetworkInterfaceType.Wireless80211 => NetworkKind.WiFi,
        NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet
            or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx => NetworkKind.Wired,
        NetworkInterfaceType.Wwanpp or NetworkInterfaceType.Wwanpp2 => NetworkKind.Mobile,
        _ => NetworkKind.Unknown,
    };

    /// <summary>
    /// Stable across reboots for the same connection, and one-way: the stored value cannot be turned back
    /// into a gateway address or an adapter identifier.
    /// </summary>
    internal static string Fingerprint(string adapterId, string? gateway)
    {
        var material = Encoding.UTF8.GetBytes($"{adapterId}|{gateway}");
        return Convert.ToHexString(SHA256.HashData(material))[..12].ToLowerInvariant();
    }
}
