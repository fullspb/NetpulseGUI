using System.Net.NetworkInformation;

namespace NetpulseGUI.Core.Collection;

public enum AdapterKind
{
    Physical,
    Wireless,
    Virtual,
    Vpn,
    Loopback,
    Tunnel
}

/// <summary>Классификация адаптеров: физический / виртуальный (Hyper-V, WSL, VM) / VPN.</summary>
public static class AdapterClassifier
{
    private static readonly string[] VirtualMarkers =
    {
        "hyper-v", "vethernet", "wsl", "virtualbox", "vmware", "virtual adapter", "virtual ethernet",
        "kernel debug", "npcap loopback", "docker", "container", "microsoft wi-fi direct virtual",
        "bluetooth device (personal area network)", "parallels", "qemu", "virtio"
    };

    private static readonly string[] VpnMarkers =
    {
        "vpn", "tap-windows", "tap-", "wireguard", "wintun", "openvpn", "fortinet", "cisco anyconnect",
        "juniper", "pangp", "globalprotect", "zerotier", "tailscale", "hamachi", "radmin", "softether",
        "outline", "amnezia", "nordlynx", "protonvpn", "sstp", "l2tp", "pptp", "ikev2"
    };

    public static AdapterKind Classify(string name, string description, NetworkInterfaceType type)
    {
        if (type == NetworkInterfaceType.Loopback)
            return AdapterKind.Loopback;

        var text = (name + " " + description).ToLowerInvariant();

        if (type is NetworkInterfaceType.Ppp || ContainsAny(text, VpnMarkers))
            return AdapterKind.Vpn;
        if (ContainsAny(text, VirtualMarkers))
            return AdapterKind.Virtual;
        if (type == NetworkInterfaceType.Tunnel)
            return AdapterKind.Tunnel;
        if (type == NetworkInterfaceType.Wireless80211)
            return AdapterKind.Wireless;
        return AdapterKind.Physical;
    }

    /// <summary>Нужно ли учитывать адаптер с учётом флагов настроек.</summary>
    public static bool ShouldInclude(AdapterKind kind, bool includeVirtual, bool includeVpn) => kind switch
    {
        AdapterKind.Loopback => false,
        AdapterKind.Tunnel => false, // Teredo/ISATAP/6to4 дублируют трафик физического адаптера
        AdapterKind.Virtual => includeVirtual,
        AdapterKind.Vpn => includeVpn,
        _ => true
    };

    private static bool ContainsAny(string text, string[] markers)
    {
        foreach (var m in markers)
            if (text.Contains(m, StringComparison.Ordinal))
                return true;
        return false;
    }
}
