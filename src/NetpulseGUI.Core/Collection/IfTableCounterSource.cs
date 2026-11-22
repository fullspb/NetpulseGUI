using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace NetpulseGUI.Core.Collection;

/// <summary>
/// Быстрое чтение счётчиков: один вызов GetIfTable2 (64-битные счётчики MIB_IF_ROW2) на тик
/// вместо NetworkInterface.GetAllNetworkInterfaces(), которое на каждый вызов перечисляет адреса
/// всех адаптеров и стоит десятки миллисекунд. Список адаптеров (имя, тип, MAC) берётся из
/// System.Net.NetworkInformation и кэшируется; обновляется по таймеру или при смене сети.
/// Идентификаторы совпадают с NetworkInterface.Id, поэтому исключения в настройках работают одинаково.
/// </summary>
public sealed class IfTableCounterSource : INetworkCounterSource, IDisposable
{
    // Смещения полей MIB_IF_ROW2 (x64/x86 одинаковы: структура выровнена по 8, размер 1352 байта).
    private const int RowSize = 1352;
    private const int OffGuid = 12;
    private const int OffFlags = 1152;
    private const int OffOperStatus = 1156;
    private const int OffRxLinkSpeed = 1200;
    private const int OffInOctets = 1208;
    private const int OffOutOctets = 1280;
    private const int TableHeader = 8; // ULONG NumEntries + выравнивание
    private const byte FilterInterfaceFlag = 0x02;

    private sealed record AdapterMeta(string Id, string Name, string Description, NetworkInterfaceType Type, string Mac);

    private readonly TimeSpan _refreshInterval;
    private Dictionary<Guid, AdapterMeta> _meta = new();
    private DateTime _metaLoadedUtc = DateTime.MinValue;
    private volatile bool _metaDirty = true;
    private readonly SystemNetworkCounterSource _fallback = new();
    private bool _useFallback;

    public IfTableCounterSource(TimeSpan? refreshInterval = null)
    {
        _refreshInterval = refreshInterval ?? TimeSpan.FromSeconds(30);
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
    }

    private void OnNetworkChanged(object? sender, EventArgs e) => _metaDirty = true;

    public IReadOnlyList<RawCounter> Read()
    {
        if (_useFallback) return _fallback.Read();

        if (_metaDirty || DateTime.UtcNow - _metaLoadedUtc > _refreshInterval)
            ReloadMeta();

        IntPtr table = IntPtr.Zero;
        try
        {
            if (GetIfTable2(out table) != 0 || table == IntPtr.Zero)
            {
                _useFallback = true;
                return _fallback.Read();
            }

            var count = Marshal.ReadInt32(table);
            var result = new List<RawCounter>(_meta.Count);
            for (var i = 0; i < count; i++)
            {
                var row = table + TableHeader + i * RowSize;
                if ((Marshal.ReadByte(row, OffFlags) & FilterInterfaceFlag) != 0) continue; // фильтр-драйверы дублируют трафик
                if (Marshal.ReadInt32(row, OffOperStatus) != 1) continue;                 // IfOperStatusUp

                var guid = ReadGuid(row + OffGuid);
                if (!_meta.TryGetValue(guid, out var m)) continue;

                result.Add(new RawCounter(
                    m.Id, m.Name, m.Description, m.Type, m.Mac,
                    ClampSpeed(Marshal.ReadInt64(row, OffRxLinkSpeed)),
                    Marshal.ReadInt64(row, OffInOctets),
                    Marshal.ReadInt64(row, OffOutOctets)));
            }

            // Новые адаптеры подхватываются по событию NetworkAddressChanged или раз в 30 сек.
            return result;
        }
        catch (Exception) when (!_useFallback)
        {
            _useFallback = true;
            return _fallback.Read();
        }
        finally
        {
            if (table != IntPtr.Zero) FreeMibTable(table);
        }
    }

    private void ReloadMeta()
    {
        var map = new Dictionary<Guid, AdapterMeta>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                if (!Guid.TryParse(ni.Id, out var g)) continue;
                map[g] = new AdapterMeta(ni.Id, ni.Name, ni.Description, ni.NetworkInterfaceType,
                    SystemNetworkCounterSource.FormatMac(ni.GetPhysicalAddress()));
            }
        }
        catch (NetworkInformationException)
        {
            return;
        }
        _meta = map;
        _metaLoadedUtc = DateTime.UtcNow;
        _metaDirty = false;
    }

    private static Guid ReadGuid(IntPtr p)
    {
        Span<byte> b = stackalloc byte[16];
        for (var i = 0; i < 16; i++) b[i] = Marshal.ReadByte(p, i);
        return new Guid(b);
    }

    private static long ClampSpeed(long v) => v < 0 || v == -1 ? 0 : v;

    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
    }

    [DllImport("iphlpapi.dll")]
    private static extern int GetIfTable2(out IntPtr table);

    [DllImport("iphlpapi.dll")]
    private static extern void FreeMibTable(IntPtr memory);
}
