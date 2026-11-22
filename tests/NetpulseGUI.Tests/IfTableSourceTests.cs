using NetpulseGUI.Core.Collection;

namespace NetpulseGUI.Tests;

/// <summary>Проверка разбора MIB_IF_ROW2 на реальной машине: значения должны совпадать с System.Net.NetworkInformation.</summary>
public class IfTableSourceTests
{
    [Fact]
    public void IfTable_MatchesNetworkInformation()
    {
        using var fast = new IfTableCounterSource();
        var reference = new SystemNetworkCounterSource().Read();
        var actual = fast.Read();

        foreach (var r in reference)
        {
            var a = actual.SingleOrDefault(x => x.Id == r.Id);
            if (a == default) continue; // у части адаптеров (например, без GUID) нет строки — допустимо
            Assert.Equal(r.Name, a.Name);
            // Счётчики читаются с разницей в доли секунды: допускаем расхождение до 50 МБ.
            Assert.InRange(a.BytesReceived - r.BytesReceived, -1, 50_000_000);
            Assert.InRange(a.BytesSent - r.BytesSent, -1, 50_000_000);
        }

        // Хотя бы один активный адаптер с ненулевым счётчиком должен совпасть.
        if (reference.Any(r => r.BytesReceived > 0))
            Assert.Contains(actual, a => reference.Any(r => r.Id == a.Id));
    }
}
