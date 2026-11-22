using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace NetpulseGUI.Core.Collection;

public sealed class AdapterDetails
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Type { get; init; } = "";
    public AdapterKind Kind { get; init; }
    public string Mac { get; init; } = "";
    public long SpeedBitsPerSec { get; init; }
    public bool IsUp { get; init; }
    public IReadOnlyList<string> IPv4 { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> IPv6 { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Gateways { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Dns { get; init; } = Array.Empty<string>();
}

/// <summary>Подробная информация об адаптерах для страницы «Адаптеры» и настроек.</summary>
public static class AdapterInventory
{
    public static IReadOnlyList<AdapterDetails> GetAll()
    {
        var result = new List<AdapterDetails>();
        NetworkInterface[] all;
        try { all = NetworkInterface.GetAllNetworkInterfaces(); }
        catch (NetworkInformationException) { return result; }

        foreach (var ni in all)
        {
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            try
            {
                var props = ni.GetIPProperties();
                var v4 = new List<string>();
                var v6 = new List<string>();
                foreach (var ua in props.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork) v4.Add(ua.Address.ToString());
                    else if (ua.Address.AddressFamily == AddressFamily.InterNetworkV6) v6.Add(ua.Address.ToString());
                }
                long speed;
                try { speed = ni.Speed; }
                catch (Exception) { speed = 0; }

                result.Add(new AdapterDetails
                {
                    Id = ni.Id,
                    Name = ni.Name,
                    Description = ni.Description,
                    Type = ni.NetworkInterfaceType.ToString(),
                    Kind = AdapterClassifier.Classify(ni.Name, ni.Description, ni.NetworkInterfaceType),
                    Mac = SystemNetworkCounterSource.FormatMac(ni.GetPhysicalAddress()),
                    SpeedBitsPerSec = speed,
                    IsUp = ni.OperationalStatus == OperationalStatus.Up,
                    IPv4 = v4,
                    IPv6 = v6,
                    Gateways = props.GatewayAddresses.Select(g => g.Address.ToString()).ToList(),
                    Dns = props.DnsAddresses.Select(d => d.ToString()).ToList()
                });
            }
            catch (NetworkInformationException)
            {
            }
        }
        return result
            .OrderByDescending(a => a.IsUp)
            .ThenBy(a => a.Kind)
            .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }
}
