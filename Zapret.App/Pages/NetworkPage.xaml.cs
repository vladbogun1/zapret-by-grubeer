using System.Net.NetworkInformation;
using System.Text;
using System.Windows.Controls;
using Zapret.App.Localization;

namespace Zapret.App.Pages;

/// <summary>
/// Network context for the current connection. Only what helps a user reason about why a strategy behaves
/// differently on another connection — no MAC addresses, no public IP, nothing that is not needed here.
/// </summary>
public partial class NetworkPage : Page
{
    public NetworkPage(ManagerClient client)
    {
        InitializeComponent();

        IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue) Render();
        };

        Render();
    }

    private void Render()
    {
        var active = NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(n => n.OperationalStatus == OperationalStatus.Up
                                 && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                                 && n.GetIPProperties().GatewayAddresses.Count > 0);

        if (active is null)
        {
            ProfileText.Text = Loc.Instance["common.noData"];
            AdapterText.Text = Loc.Instance["common.noData"];
            return;
        }

        ProfileText.Text = active.NetworkInterfaceType switch
        {
            NetworkInterfaceType.Wireless80211 => Loc.Instance["network.wifi"],
            NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet => Loc.Instance["network.ethernet"],
            NetworkInterfaceType.Wwanpp or NetworkInterfaceType.Wwanpp2 => Loc.Instance["network.mobile"],
            _ => active.NetworkInterfaceType.ToString(),
        };

        var properties = active.GetIPProperties();
        var gateway = properties.GatewayAddresses.FirstOrDefault()?.Address?.ToString() ?? Loc.Instance["common.noData"];
        var dns = properties.DnsAddresses.Count == 0
            ? Loc.Instance["common.noData"]
            : string.Join(", ", properties.DnsAddresses.Take(2).Select(a => a.ToString()));

        AdapterText.Text = new StringBuilder()
            .AppendLine(active.Name)
            .AppendLine($"Gateway   {gateway}")
            .Append($"DNS       {dns}")
            .ToString();
    }
}
