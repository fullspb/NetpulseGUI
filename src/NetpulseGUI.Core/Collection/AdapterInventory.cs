using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Win32;

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

        var visible = ControlPanelAdapterIds();
        foreach (var ni in all)
        {
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            if (!IsUserVisible(ni.Id, ni.Name, ni.Description, visible)) continue;
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

    private const string NetClassGuid = "{4D36E972-E325-11CE-BFC1-08002BE10318}";
    private const int NcfHidden = 0x8;

    /// <summary>
    /// GUID адаптеров, которые видны в «Панель управления → Сетевые подключения»:
    /// у них есть ключ Network\{класс}\{GUID}\Connection, а в ключе драйвера нет флага NCF_HIDDEN.
    /// Служебные интерфейсы (фильтры QoS/WFP, WAN Miniport, Wi-Fi Direct, Teredo и т. п.) туда не попадают.
    /// Возвращает null, если реестр прочитать не удалось.
    /// </summary>
    public static HashSet<string>? ControlPanelAdapterIds()
    {
        try
        {
            var hidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var registered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\" + NetClassGuid))
            {
                if (cls == null) return null;
                foreach (var sub in cls.GetSubKeyNames())
                {
                    try
                    {
                        using var k = cls.OpenSubKey(sub);
                        if (k?.GetValue("NetCfgInstanceId") is not string id) continue;
                        registered.Add(id);
                        var characteristics = k.GetValue("Characteristics") is int c ? c : 0;
                        if ((characteristics & NcfHidden) != 0) hidden.Add(id);
                    }
                    catch (System.Security.SecurityException)
                    {
                        // Ключ «Properties» и подобные закрыты для чтения — пропускаем.
                    }
                }
            }

            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var net = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Network\" + NetClassGuid);
            if (net == null) return null;
            foreach (var id in net.GetSubKeyNames())
            {
                if (!registered.Contains(id) || hidden.Contains(id)) continue;
                using var conn = net.OpenSubKey(id + @"\Connection");
                if (conn != null) result.Add(id);
            }
            return result.Count > 0 ? result : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static readonly string[] ServiceMarkers =
    {
        "QoS Packet Scheduler", "LightWeight Filter", "WFP ", "WAN Miniport", "Kernel Debug",
        "Teredo", "ISATAP", "6to4", "IP-HTTPS", "Wi-Fi Direct Virtual"
    };

    /// <summary>Показывать ли адаптер пользователю (в настройках и списках).</summary>
    public static bool IsUserVisible(string id, string name, string description, HashSet<string>? controlPanelIds)
    {
        if (controlPanelIds != null && !controlPanelIds.Contains(id)) return false;
        // Дополнительно (и как запасной вариант без реестра) отсекаем служебные интерфейсы по названию.
        foreach (var m in ServiceMarkers)
            if (name.Contains(m, StringComparison.OrdinalIgnoreCase) || description.Contains(m, StringComparison.OrdinalIgnoreCase))
                return false;
        return true;
    }
}
