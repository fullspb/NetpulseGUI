using System.Net.NetworkInformation;
using NetpulseGUI.Core.Collection;

namespace NetpulseGUI.Tests;

public class AdapterMonitorTests
{
    private sealed class FakeSource : INetworkCounterSource
    {
        public List<RawCounter> Counters { get; } = new();
        public IReadOnlyList<RawCounter> Read() => Counters.ToList();

        public void Set(string id, long rx, long tx, string desc = "Intel Ethernet", NetworkInterfaceType type = NetworkInterfaceType.Ethernet)
        {
            Counters.RemoveAll(c => c.Id == id);
            Counters.Add(new RawCounter(id, id, desc, type, "00:11:22:33:44:55", 1_000_000_000, rx, tx));
        }
    }

    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void FirstTick_IsBaseline()
    {
        var src = new FakeSource();
        src.Set("eth", 1000, 500);
        var m = new AdapterMonitor(src);
        var r = m.Tick(T0);
        Assert.True(r.IsBaseline);
        Assert.Empty(r.Deltas);
    }

    [Fact]
    public void SecondTick_ComputesDelta()
    {
        var src = new FakeSource();
        src.Set("eth", 1000, 500);
        var m = new AdapterMonitor(src);
        m.Tick(T0);
        src.Set("eth", 4000, 700);
        var r = m.Tick(T0.AddSeconds(1));
        Assert.Equal(3000, r.TotalRx);
        Assert.Equal(200, r.TotalTx);
        Assert.Equal(1.0, r.ElapsedSeconds, 3);
    }

    [Fact]
    public void Wraparound_IsHandled()
    {
        var src = new FakeSource();
        src.Set("eth", uint.MaxValue - 9, 0);
        var m = new AdapterMonitor(src);
        m.Tick(T0);
        src.Set("eth", 10, 0);
        Assert.Equal(20, m.Tick(T0.AddSeconds(1)).TotalRx);
    }

    [Fact]
    public void RecreatedAdapter_StartsFromNewBaseline()
    {
        var src = new FakeSource();
        src.Set("eth", 5_000_000, 0);
        var m = new AdapterMonitor(src);
        m.Tick(T0);

        src.Counters.Clear(); // адаптер исчез
        Assert.Equal(0, m.Tick(T0.AddSeconds(1)).TotalRx);

        // Появился снова со сброшенными счётчиками — первая точка опорная, без всплеска.
        src.Set("eth", 100, 0);
        Assert.Equal(0, m.Tick(T0.AddSeconds(2)).TotalRx);

        src.Set("eth", 600, 0);
        Assert.Equal(500, m.Tick(T0.AddSeconds(3)).TotalRx);
    }

    [Fact]
    public void ExcludedAdapter_IsNotReported_ButTracked()
    {
        var src = new FakeSource();
        src.Set("eth", 0, 0);
        src.Set("wifi", 0, 0);
        var m = new AdapterMonitor(src) { Filter = AdapterFilter.Create(new[] { "wifi" }, false, false) };
        m.Tick(T0);
        src.Set("eth", 100, 0);
        src.Set("wifi", 1000, 0);
        var r = m.Tick(T0.AddSeconds(1));
        Assert.Equal(100, r.TotalRx);
        Assert.Single(r.Deltas);

        // Снимаем исключение — без всплеска накопленного трафика.
        m.Filter = AdapterFilter.Default;
        src.Set("wifi", 1100, 0);
        Assert.Equal(100, m.Tick(T0.AddSeconds(2)).Deltas.Single(d => d.Id == "wifi").Rx);
    }

    [Fact]
    public void VirtualAdapters_FilteredByDefault()
    {
        var src = new FakeSource();
        src.Set("hv", 0, 0, "Hyper-V Virtual Ethernet Adapter");
        src.Set("vpn", 0, 0, "WireGuard Tunnel");
        var m = new AdapterMonitor(src);
        m.Tick(T0);
        src.Set("hv", 100, 0, "Hyper-V Virtual Ethernet Adapter");
        src.Set("vpn", 100, 0, "WireGuard Tunnel");
        Assert.Equal(0, m.Tick(T0.AddSeconds(1)).TotalRx);

        m.Filter = AdapterFilter.Create(Array.Empty<string>(), includeVirtual: true, includeVpn: true);
        src.Set("hv", 200, 0, "Hyper-V Virtual Ethernet Adapter");
        src.Set("vpn", 200, 0, "WireGuard Tunnel");
        Assert.Equal(200, m.Tick(T0.AddSeconds(2)).TotalRx);
    }

    [Fact]
    public void LongGap_SleepResume_IsBaseline()
    {
        var src = new FakeSource();
        src.Set("eth", 0, 0);
        var m = new AdapterMonitor(src);
        m.Tick(T0);
        src.Set("eth", 999_999, 0);
        var r = m.Tick(T0.AddHours(2));
        Assert.True(r.IsBaseline);
        Assert.Equal(0, r.TotalRx);
    }

    [Fact]
    public void ClockGoingBackwards_IsBaseline()
    {
        var src = new FakeSource();
        src.Set("eth", 0, 0);
        var m = new AdapterMonitor(src);
        m.Tick(T0);
        src.Set("eth", 100, 0);
        Assert.True(m.Tick(T0.AddSeconds(-30)).IsBaseline);
    }

    [Theory]
    [InlineData("vEthernet (WSL)", "Hyper-V Virtual Ethernet Adapter", NetworkInterfaceType.Ethernet, AdapterKind.Virtual)]
    [InlineData("Ethernet", "Realtek PCIe GbE Family Controller", NetworkInterfaceType.Ethernet, AdapterKind.Physical)]
    [InlineData("Wi-Fi", "Intel(R) Wi-Fi 6 AX201", NetworkInterfaceType.Wireless80211, AdapterKind.Wireless)]
    [InlineData("OpenVPN", "TAP-Windows Adapter V9", NetworkInterfaceType.Ethernet, AdapterKind.Vpn)]
    [InlineData("Teredo", "Teredo Tunneling Pseudo-Interface", NetworkInterfaceType.Tunnel, AdapterKind.Tunnel)]
    [InlineData("lo", "Loopback", NetworkInterfaceType.Loopback, AdapterKind.Loopback)]
    public void Classifier_Works(string name, string desc, NetworkInterfaceType type, AdapterKind expected)
    {
        Assert.Equal(expected, AdapterClassifier.Classify(name, desc, type));
    }
}
