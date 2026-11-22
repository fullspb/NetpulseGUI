using NetpulseGUI.Core.Counters;

namespace NetpulseGUI.Tests;

public class CounterMathTests
{
    [Fact]
    public void NormalIncrement_ReturnsDifference()
    {
        var r = CounterMath.ComputeDelta(1_000, 5_000, 1.0);
        Assert.Equal(4_000, r.Delta);
        Assert.Equal(CounterChange.Normal, r.Change);
    }

    [Fact]
    public void NoChange_ReturnsZero()
    {
        var r = CounterMath.ComputeDelta(123, 123, 1.0);
        Assert.Equal(0, r.Delta);
        Assert.Equal(CounterChange.Normal, r.Change);
    }

    [Fact]
    public void Wrap32_IsDetected()
    {
        long prev = uint.MaxValue - 99; // 100 байт до переполнения
        long cur = 400;                  // и 400 после
        var r = CounterMath.ComputeDelta(prev, cur, 1.0, 1_000_000_000);
        Assert.Equal(CounterChange.Wrap32, r.Change);
        Assert.Equal(500, r.Delta);
    }

    [Fact]
    public void Wrap32_ExactBoundary()
    {
        var r = CounterMath.ComputeDelta(uint.MaxValue, 0, 1.0);
        Assert.Equal(CounterChange.Wrap32, r.Change);
        Assert.Equal(1, r.Delta);
    }

    [Fact]
    public void Reset_On64BitCounter_UsesCurrentValue()
    {
        // 64-битный счётчик (больше 2^32) уменьшился — это сброс, а не переполнение.
        var r = CounterMath.ComputeDelta(10_000_000_000, 2_000, 1.0);
        Assert.Equal(CounterChange.Reset, r.Change);
        Assert.Equal(2_000, r.Delta);
    }

    [Fact]
    public void Reset_WithSmallValues_WhenWrapIsImplausible()
    {
        // Падение со 100 МБ до 50 КБ на линке 10 Мбит/с: «переполнение» дало бы ~4 ГБ за секунду — невозможно.
        var r = CounterMath.ComputeDelta(100_000_000, 50_000, 1.0, 10_000_000);
        Assert.Equal(CounterChange.Reset, r.Change);
        Assert.Equal(50_000, r.Delta);
    }

    [Fact]
    public void ImplausibleJump_IsDiscarded()
    {
        // Линк 1 Гбит/с не может дать 10 ГБ за секунду.
        var r = CounterMath.ComputeDelta(0, 10_000_000_000, 1.0, 1_000_000_000);
        Assert.Equal(CounterChange.Discarded, r.Change);
        Assert.Equal(0, r.Delta);
    }

    [Fact]
    public void PlausibleJump_OverLongerInterval_IsAccepted()
    {
        var r = CounterMath.ComputeDelta(0, 1_000_000_000, 10.0, 1_000_000_000);
        Assert.Equal(CounterChange.Normal, r.Change);
        Assert.Equal(1_000_000_000, r.Delta);
    }

    [Fact]
    public void NegativeValues_AreDiscarded()
    {
        Assert.Equal(CounterChange.Discarded, CounterMath.ComputeDelta(-1, 5, 1).Change);
        Assert.Equal(CounterChange.Discarded, CounterMath.ComputeDelta(5, -1, 1).Change);
    }

    [Fact]
    public void UnknownLinkSpeed_UsesGlobalCap()
    {
        Assert.Equal(CounterMath.DefaultMaxBytesPerSecond, CounterMath.MaxPlausibleBytes(1.0, 0));
    }
}
