using Aion2Meter.App;
using Xunit;

namespace Aion2Meter.App.Tests;

public sealed class HotkeyTests
{
    private sealed class Registrar : IHotkeyRegistrar
    {
        public readonly Dictionary<int, (uint Modifiers, uint Key)> Active = [];
        public readonly List<int> Released = [];
        public int RejectId;
        public bool Register(int id, uint modifiers, uint key)
        { if (id == RejectId) return false; Active.Add(id, (modifiers, key)); return true; }
        public void Unregister(int id) { Active.Remove(id); Released.Add(id); }
    }
    [Fact]
    public void BothBindingsDispatchUseNoRepeatAndReleaseOnRebindAndDispose()
    {
        var native = new Registrar(); var hidden = 0; var reset = 0;
        using var keys = new HotkeyBindings(native, () => hidden++, () => reset++);
        Assert.Equal("", keys.Apply("Ctrl+Shift+H", "Ctrl+Shift+R"));
        Assert.True(keys.CanToggle);
        Assert.All(native.Active.Values, v => Assert.Equal(0x4000u, v.Modifiers & 0x4000));
        keys.Dispatch(1); keys.Dispatch(2); keys.Dispatch(999); Assert.Equal(1, hidden); Assert.Equal(1, reset);
        keys.Apply("Alt+H", "Alt+R"); Assert.Equal(2, native.Released.Count); Assert.Equal(2, native.Active.Count);
        keys.Dispose(); keys.Dispose(); Assert.Empty(native.Active); Assert.Equal(4, native.Released.Count);
        Assert.False(keys.CanToggle);
        keys.Dispatch(1); Assert.Equal(1, hidden);
    }
    [Fact]
    public void ConflictIsVisibleInvalidAndDuplicateGesturesKeepExistingBindings()
    {
        var native = new Registrar { RejectId = 1 }; var hidden = 0; var reset = 0;
        using var keys = new HotkeyBindings(native, () => hidden++, () => reset++);
        Assert.Contains("unavailable", keys.Apply("Ctrl+Shift+H", "Ctrl+Shift+R"));
        Assert.False(keys.CanToggle);
        keys.Dispatch(1); keys.Dispatch(2); Assert.Equal(0, hidden); Assert.Equal(1, reset);
        Assert.Contains("different", keys.Apply("alt+h", "ALT+H")); Assert.Single(native.Active);
        Assert.Contains("Use", keys.Apply("bad", "Ctrl+R")); Assert.Single(native.Active);
        keys.Dispose(); Assert.Single(native.Released); Assert.Empty(native.Active);
    }
}
