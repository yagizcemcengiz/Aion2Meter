using System.Text.Json;
using Aion2Meter.Core;
using Aion2Meter.Presentation;
using Xunit;
using static Aion2Meter.Tests.LiveCombatMeterTests;

namespace Aion2Meter.Tests;

public sealed class OverlaySelfClassTests
{
    [Theory]
    [InlineData(true, PlayerClass.Unknown, null, PlayerClass.Unknown)]
    [InlineData(true, PlayerClass.Unknown, PlayerClass.Cleric, PlayerClass.Cleric)]
    [InlineData(true, PlayerClass.Templar, PlayerClass.Cleric, PlayerClass.Templar)]
    [InlineData(false, PlayerClass.Unknown, PlayerClass.Cleric, PlayerClass.Unknown)]
    [InlineData(false, PlayerClass.Ranger, PlayerClass.Cleric, PlayerClass.Ranger)]
    [InlineData(true, PlayerClass.Unknown, PlayerClass.Unknown, PlayerClass.Unknown)]
    [InlineData(true, PlayerClass.Unknown, (PlayerClass)999, PlayerClass.Unknown)]
    public void AuthoritativeClassWinsAndManualFallbackIsOnlyForSelf(bool self, PlayerClass raw, PlayerClass? selected, PlayerClass expected)
    {
        var row = new OverlayRow("scope/1", 3, "Player", self, 218182, 3220, 31.2m, 2, raw);
        var view = new OverlayRowViewModel(row, selected);
        Assert.Equal(expected, view.Class); Assert.Equal(raw, row.Class);
        Assert.Equal("218K", view.Damage); Assert.Equal("3.22K", view.Dps);
        Assert.Equal(3, view.Rank); Assert.Equal(31.2, view.ContributionValue);
        Assert.Equal(self, view.IsSelf);
    }

    [Fact]
    public void PreferenceReprojectsCachedRowsImmediatelyAndSurvivesNewEpochWithoutBackendMutation()
    {
        var h = Fresh(); h.Frame(Hit(600)); var before = h.Tick();
        var snapshot = OverlaySnapshot.FromMeter(before);
        var view = new OverlayViewModel(); view.Apply(snapshot); var reused = view.Rows[0];
        view.SetSelfClassOverride(PlayerClass.Cleric);
        Assert.Same(reused, view.Rows[0]); Assert.Equal(PlayerClass.Cleric, reused.Class);
        Assert.Contains("display preference", reused.ClassHint);
        view.Apply(snapshot); Assert.Equal(PlayerClass.Cleric, reused.Class);
        Assert.Equal(PlayerClass.Unknown, snapshot.Rows[0].Class);
        Assert.DoesNotContain("SelfClassOverride", JsonSerializer.Serialize(snapshot));
        var after = h.Tick();
        Assert.Equal(before.TotalDamage, after.TotalDamage); Assert.Equal(before.EntityId, after.EntityId);
        Assert.Equal(before.EpochId, after.EpochId); Assert.Equal(before.EncounterSelfHits, after.EncounterSelfHits);
        view.Apply(OverlaySnapshot.Waiting); Assert.Empty(view.Rows);
        view.Apply(snapshot with { Rows = Array.AsReadOnly(new[] { snapshot.Rows[0] with { Key = "new-epoch/1" } }) });
        Assert.Equal(PlayerClass.Cleric, view.Rows[0].Class);
        view.SetSelfClassOverride(null); Assert.Equal(PlayerClass.Unknown, view.Rows[0].Class);
    }

    [Fact]
    public async Task ManualClassRoundTripsOnlyInLocalPreferencesAndOldOrInvalidSelectionsRemainAutomatic()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); var path = Path.Combine(directory, "settings.json");
        try
        {
            var store = new OverlaySettingsStore(path);
            File.WriteAllText(path, "{\"Width\":550,\"Opacity\":0.7}");
            Assert.Null(store.Load().Settings.SelfClassOverride);
            foreach (var playerClass in Enum.GetValues<PlayerClass>().Where(c => c != PlayerClass.Unknown))
            {
                var settings = new OverlaySettings(Width: 550, Opacity: .7, SelfClassOverride: playerClass);
                await store.SaveAsync(settings); Assert.Equal(settings, store.Load().Settings);
            }
            foreach (var invalid in new[] { PlayerClass.Unknown, (PlayerClass)999, (PlayerClass)(-1) })
            {
                File.WriteAllText(path, JsonSerializer.Serialize(new OverlaySettings(SelfClassOverride: invalid)));
                Assert.Null(store.Load().Settings.SelfClassOverride);
            }
            await store.SaveAsync(new()); Assert.Null(store.Load().Settings.SelfClassOverride);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(3220, "3.22K")] [InlineData(218182, "218K")]
    [InlineData(1240000000, "1.24B")]
    public void CompactTotalsKeepFullPrecision(decimal amount, string expected)
    {
        var row = new OverlayRow("scope/1", 1, "Player", true, amount, null, 100, 1);
        Assert.Equal(expected, new OverlayRowViewModel(row).Damage); Assert.Equal(amount, row.TotalDamage);
    }
}
