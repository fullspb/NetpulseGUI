using System.Net.NetworkInformation;
using NetpulseGUI.Core.Collection;
using Xunit;
using Xunit.Abstractions;

namespace NetpulseGUI.Tests;

public class AdapterInventoryTests
{
    private readonly ITestOutputHelper _out;
    public AdapterInventoryTests(ITestOutputHelper output) => _out = output;

    [Theory]
    [InlineData("Ethernet-QoS Packet Scheduler-0000", "Intel(R) Ethernet Controller I226-V-QoS Packet Scheduler-0000")]
    [InlineData("Ethernet-WFP Native MAC Layer LightWeight Filter-0000", "Intel(R) I226-V-WFP Native MAC Layer LightWeight Filter-0000")]
    [InlineData("Подключение по локальной сети* 6", "WAN Miniport (IP)")]
    [InlineData("Подключение по локальной сети* 1", "Microsoft Wi-Fi Direct Virtual Adapter")]
    public void FallbackHidesServiceInterfaces(string name, string description) =>
        Assert.False(AdapterInventory.IsUserVisible("{X}", name, description, null));

    [Theory]
    [InlineData("Ethernet", "Intel(R) Ethernet Controller I226-V")]
    [InlineData("Ethernet 4", "VirtualBox Host-Only Ethernet Adapter")]
    [InlineData("Беспроводная сеть", "Intel(R) Wi-Fi 6 AX201 160MHz")]
    public void FallbackKeepsRealAdapters(string name, string description) =>
        Assert.True(AdapterInventory.IsUserVisible("{X}", name, description, null));

    [Fact]
    public void RegistryListDecidesWhenAvailable()
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "{A}" };
        Assert.True(AdapterInventory.IsUserVisible("{a}", "Ethernet 5", "Realtek PCIe GbE", ids));
        Assert.False(AdapterInventory.IsUserVisible("{B}", "Ethernet", "Intel", ids));
    }

    [Fact]
    public void LiveListHasNoServiceInterfaces()
    {
        var ids = AdapterInventory.ControlPanelAdapterIds();
        Assert.NotNull(ids);
        var all = NetworkInterface.GetAllNetworkInterfaces();
        var shown = AdapterInventory.GetAll();
        _out.WriteLine($"interfaces: {all.Length}, shown: {shown.Count}");
        foreach (var a in shown) _out.WriteLine($"  {a.Name} | {a.Description}");
        Assert.NotEmpty(shown);
        Assert.All(shown, a => Assert.DoesNotContain("Filter", a.Description));
        Assert.All(shown, a => Assert.DoesNotContain("Packet Scheduler", a.Description));
    }
}
