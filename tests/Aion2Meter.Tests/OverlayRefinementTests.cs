using Aion2Meter.Core;
using Aion2Meter.Presentation;
using Xunit;
using static Aion2Meter.Tests.LiveCombatMeterTests;

namespace Aion2Meter.Tests;

public sealed class OverlayRefinementTests
{
    [Theory]
    [InlineData(0, "0.0")] [InlineData(1846.1, "1.85K")] [InlineData(65744, "65.7K")]
    [InlineData(1400000, "1.40M")] [InlineData(999950, "1.00M")]
    public void CompactRatesPreserveReadablePrecision(double amount, string text) => Assert.Equal(text, CompactNumber.Format((decimal)amount, rate: true));

    [Theory]
    [InlineData(0, 4)] [InlineData(70, 4)] [InlineData(70.01, 3)] [InlineData(120, 3)]
    [InlineData(120.01, 2)] [InlineData(180, 2)] [InlineData(180.01, 1)] [InlineData(900, 1)]
    public void SmoothedRttChoosesSignalThresholds(double ms, int bars)
    {
        var view = new OverlayViewModel(); view.Apply(OverlaySnapshot.Stopped with { NetworkRttMilliseconds = ms });
        Assert.Equal(bars, view.ActiveSignalBars); Assert.Contains("ms", view.Ping);
        Assert.NotEqual(LatencyQuality.Inactive, view.Signal1);
        Assert.Equal(bars == 4 ? "#71D6A6" : bars == 3 ? "#E7C86D" : "#EF8B89", view.Signal1);
        if (bars < 4) Assert.Equal(LatencyQuality.Inactive, view.Signal4);
    }

    [Theory]
    [InlineData(null)] [InlineData(-1d)] [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)]
    public void MissingOrInvalidRttHasNoActiveBars(double? ms)
    {
        var view = new OverlayViewModel(); view.Apply(OverlaySnapshot.Stopped with { NetworkRttMilliseconds = 85 });
        view.Apply(OverlaySnapshot.Stopped with { NetworkRttMilliseconds = ms });
        Assert.Equal(0, view.ActiveSignalBars); Assert.Equal("— ms", view.Ping);
        Assert.All(new[] { view.Signal1, view.Signal2, view.Signal3, view.Signal4 }, c => Assert.Equal(LatencyQuality.Inactive, c));
    }

    [Fact]
    public void ClassAndFormattingAreImmutablePresentationOnlyAndDoNotChangeSortOrAmount()
    {
        var h = Fresh(); h.Frame(Hit(1846)); var snapshot = OverlaySnapshot.FromMeter(h.Tick());
        Assert.Equal(PlayerClass.Unknown, snapshot.Rows[0].Class);
        var row = snapshot.Rows[0] with { Dps = 1846.123456m, Class = PlayerClass.Cleric };
        var view = new OverlayRowViewModel(row); Assert.Equal("1.85K", view.Dps); Assert.Equal("1.85K", view.Damage);
        Assert.Equal(1846.123456m, row.Dps); Assert.Equal(PlayerClass.Cleric, view.Class);
        Assert.Equal(1846m, h.Tick().TotalDamage); Assert.Equal(100d, view.ContributionValue);
        Assert.Equal(row.Rank, view.Rank); Assert.True(view.IsSelf);
        view.Apply(row with { Class = (PlayerClass)999 }); Assert.Equal(PlayerClass.Unknown, view.Class);
    }
}
