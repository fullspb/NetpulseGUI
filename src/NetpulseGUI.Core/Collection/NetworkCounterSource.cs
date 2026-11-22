using System.Net.NetworkInformation;

namespace NetpulseGUI.Core.Collection;

/// <summary>Сырое показание счётчиков одного адаптера.</summary>
public readonly record struct RawCounter(
    string Id,
    string Name,
    string Description,
    NetworkInterfaceType Type,
    string Mac,
    long SpeedBitsPerSec,
    long BytesReceived,
    long BytesSent);

public interface INetworkCounterSource
{
    IReadOnlyList<RawCounter> Read();
}

/// <summary>Чтение счётчиков через System.Net.NetworkInformation.</summary>
public sealed class SystemNetworkCounterSource : INetworkCounterSource
{
    public IReadOnlyList<RawCounter> Read()
    {
        NetworkInterface[] all;
        try
        {
            all = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException)
        {
            return Array.Empty<RawCounter>();
        }

        var list = new List<RawCounter>(all.Length);
        foreach (var ni in all)
        {
            try
            {
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback || ni.OperationalStatus != OperationalStatus.Up)
                    continue;

                // На Windows GetIPStatistics() и GetIPv4Statistics() читают MIB_IF_ROW2 — весь трафик интерфейса (IPv4 + IPv6).
                var stats = ni.GetIPStatistics();
                long speed;
                try { speed = ni.Speed; }
                catch (Exception) { speed = 0; }

                list.Add(new RawCounter(
                    ni.Id,
                    ni.Name,
                    ni.Description,
                    ni.NetworkInterfaceType,
                    FormatMac(ni.GetPhysicalAddress()),
                    speed,
                    stats.BytesReceived,
                    stats.BytesSent));
            }
            catch (NetworkInformationException)
            {
                // Адаптер исчез между перечислением и чтением.
            }
            catch (PlatformNotSupportedException)
            {
            }
        }
        return list;
    }

    public static string FormatMac(PhysicalAddress? address)
    {
        if (address == null) return "";
        var bytes = address.GetAddressBytes();
        return bytes.Length == 0 ? "" : string.Join(":", bytes.Select(b => b.ToString("X2")));
    }
}
