// SPDX-License-Identifier: AGPL-3.0-only

using CouchSubs.Shell;

namespace CouchSubs.Shell.Tests;

public class HotkeyTableTests
{
    /// Varsayılan olarak hiçbir işin kısayolu önceden seçili gelmez; kullanıcı istediği tuşları kendisi atar.
    [Fact]
    public void NoShortcutsAreSelectedByDefault()
    {
        Assert.Empty(HotkeyTable.Bindings);
        Assert.Empty(HotkeyTable.Resolve(null));
        Assert.Empty(HotkeyTable.Resolve([]));
        foreach (var action in Enum.GetValues<HotkeyAction>())
        {
            Assert.Null(HotkeyTable.DefaultKey(action, 0));
            Assert.Null(HotkeyTable.DefaultKey(action, 1));
            Assert.Empty(HotkeyTable.KeysFor(null, action));
        }
    }

    [Fact]
    public void ChangedKeySetsOnlyThatSlot()
    {
        var altF2 = new HotkeyKey(HotkeyModifiers.Alt, 0x71);
        var choices = HotkeyTable.Assign([], HotkeyAction.Screen, 0, altF2, out var displaced);
        Assert.Null(displaced);
        Assert.Equal([altF2], HotkeyTable.KeysFor(choices, HotkeyAction.Screen));
        Assert.Single(HotkeyTable.Resolve(choices));
        Assert.Equal("Alt+F2", HotkeyTable.Resolve(choices).Single().Label);
    }

    /// Başka işin tuşu seçilirse o işten kaldırılır; iki iş aynı tuşu tutmaz.
    [Fact]
    public void TakenKeyMovesFromOtherAction()
    {
        var num5 = new HotkeyKey(HotkeyModifiers.None, 0x65);
        var ctrlShift5 = new HotkeyKey(HotkeyTable.ComboModifiers, 0x35);
        var choices = HotkeyTable.Assign([], HotkeyAction.FreeArea, 0, num5, out _);
        choices = HotkeyTable.Assign(choices, HotkeyAction.FreeArea, 1, ctrlShift5, out _);
        choices = HotkeyTable.Assign(choices, HotkeyAction.Screen, 0, num5, out var displaced);
        Assert.Equal(HotkeyAction.FreeArea, displaced);
        Assert.Equal(HotkeyAction.Screen, HotkeyTable.Resolve(choices).Single(b => b.VirtualKey == 0x65).Action);
        Assert.Null(HotkeyTable.KeyFor(choices, HotkeyAction.FreeArea, 0));
        Assert.Equal([ctrlShift5], HotkeyTable.KeysFor(choices, HotkeyAction.FreeArea));
    }

    /// Kısayol kapatılabilir; tüm tuşlar temizlenince liste tekrar boş kalır.
    [Fact]
    public void ClearingAndRestoringKeys()
    {
        var f6 = new HotkeyKey(HotkeyModifiers.None, 0x75);
        var assigned = HotkeyTable.Assign([], HotkeyAction.ClickThrough, 0, f6, out _);
        Assert.Single(HotkeyTable.KeysFor(assigned, HotkeyAction.ClickThrough));
        var cleared = HotkeyTable.Assign(assigned, HotkeyAction.ClickThrough, 0, null, out _);
        Assert.Empty(HotkeyTable.KeysFor(cleared, HotkeyAction.ClickThrough));
        Assert.Empty(cleared);
    }

    /// Ayar dosyası elle bozulsa da (aynı tuş iki işte, geçersiz tuş) tutulan tabloda çakışma olmaz.
    [Fact]
    public void ResolveSkipsDuplicateAndInvalidChoices()
    {
        HotkeyChoice[] choices =
        [
            new(HotkeyAction.Screen, 0, new HotkeyKey(HotkeyModifiers.None, 0x69)),
            new(HotkeyAction.Screen, 1, new HotkeyKey(HotkeyModifiers.None, 0x41)),
            new(HotkeyAction.FreeArea, 1, new HotkeyKey(HotkeyModifiers.None, 0x69)),
        ];
        var bindings = HotkeyTable.Resolve(choices);
        Assert.Equal(bindings.Count, bindings.Select(b => (b.Modifiers, b.VirtualKey)).Distinct().Count());
        Assert.DoesNotContain(bindings, b => b.VirtualKey == 0x41);
        Assert.Equal(HotkeyAction.Screen, bindings.Single(b => b.VirtualKey == 0x69).Action);
    }

    [Theory]
    [InlineData(HotkeyModifiers.None, 0x69u, HotkeyProblem.None)]          // Num 9
    [InlineData(HotkeyModifiers.None, 0x71u, HotkeyProblem.None)]          // F2
    [InlineData(HotkeyModifiers.None, 0x7Bu, HotkeyProblem.UnsupportedKey)] // F12 hata ayıklayıcıya ayrılmış
    [InlineData(HotkeyModifiers.None, 0x1Bu, HotkeyProblem.UnsupportedKey)] // Esc
    [InlineData(HotkeyModifiers.None, 0x54u, HotkeyProblem.NeedsModifier)]  // T
    [InlineData(HotkeyModifiers.Shift, 0x54u, HotkeyProblem.NeedsModifier)] // Shift+T büyük harf yazar
    [InlineData(HotkeyModifiers.Control, 0x43u, HotkeyProblem.NeedsModifier)] // Ctrl+C kopyalar
    [InlineData(HotkeyModifiers.Alt, 0x54u, HotkeyProblem.None)]            // Alt+T
    [InlineData(HotkeyModifiers.Alt | HotkeyModifiers.Shift, 0x54u, HotkeyProblem.None)]
    [InlineData(HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x54u, HotkeyProblem.None)]
    [InlineData(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x33u, HotkeyProblem.AltGr)] // AltGr+3 = #
    [InlineData(HotkeyModifiers.Alt, 0x73u, HotkeyProblem.Reserved)]       // Alt+F4
    public void Validation(HotkeyModifiers modifiers, uint vk, HotkeyProblem expected) =>
        Assert.Equal(expected, HotkeyTable.Validate(new HotkeyKey(modifiers, vk)));

    /// Varsayılan tuşların hepsi kendi kuralından geçer.
    [Fact]
    public void DefaultsAreValid() =>
        Assert.All(HotkeyTable.Bindings, b => Assert.Equal(HotkeyProblem.None, HotkeyTable.Validate(new HotkeyKey(b.Modifiers, b.VirtualKey))));

    /// Seçim ayar dosyasına yazılıp geri okunur (enumlar adla).
    [Fact]
    public void ChoicesSurviveSettingsFile()
    {
        var choices = HotkeyTable.Assign([], HotkeyAction.Screen, 0, new HotkeyKey(HotkeyModifiers.Alt | HotkeyModifiers.Shift, 0x53), out _);
        choices = HotkeyTable.Assign(choices, HotkeyAction.Screen, 1, null, out _);
        var json = System.Text.Json.JsonSerializer.Serialize(new UserSettings { Hotkeys = choices });
        Assert.Contains("\"Screen\"", json);
        var loaded = System.Text.Json.JsonSerializer.Deserialize<UserSettings>(json)!;
        Assert.Equal(HotkeyTable.Resolve(choices), HotkeyTable.Resolve(loaded.Hotkeys));
        Assert.Equal([new HotkeyKey(HotkeyModifiers.Alt | HotkeyModifiers.Shift, 0x53)], HotkeyTable.KeysFor(loaded.Hotkeys, HotkeyAction.Screen));
        Assert.Empty(System.Text.Json.JsonSerializer.Deserialize<UserSettings>("{}")!.Hotkeys);
    }

    /// Çeviri kapalıyken yalnız Başlat/Durdur kısayolları tutulur; diğer tuşlar ancak çeviri açılınca yakalanır.
    [Fact]
    public void ActiveBindingsKeepOnlyToggleWhileStopped()
    {
        var f8 = new HotkeyKey(HotkeyModifiers.None, 0x77);
        var f9 = new HotkeyKey(HotkeyModifiers.None, 0x78);
        var choices = HotkeyTable.Assign([], HotkeyAction.ToggleTranslation, 0, f8, out _);
        choices = HotkeyTable.Assign(choices, HotkeyAction.Screen, 0, f9, out _);

        var stopped = HotkeyTable.ActiveBindings(choices, isRunning: false);
        Assert.Single(stopped);
        Assert.Equal(HotkeyAction.ToggleTranslation, stopped[0].Action);
        Assert.Equal(0x77u, stopped[0].VirtualKey);

        var running = HotkeyTable.ActiveBindings(choices, isRunning: true);
        Assert.Equal(2, running.Count);
        Assert.Contains(running, b => b.Action == HotkeyAction.ToggleTranslation && b.VirtualKey == 0x77u);
        Assert.Contains(running, b => b.Action == HotkeyAction.Screen && b.VirtualKey == 0x78u);
    }
}
