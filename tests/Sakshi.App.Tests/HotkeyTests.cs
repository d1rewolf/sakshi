using Sakshi.App.Tray;

namespace Sakshi.App.Tests;

public sealed class HotkeyTests
{
    [Fact]
    public void The_default_is_ctrl_alt_r() =>
        Assert.Equal("Ctrl+Alt+R", new Sakshi.Core.Config.SakshiConfig().Shortcut);

    [Theory]
    [InlineData("Ctrl+Alt+R", Hotkey.Ctrl | Hotkey.Alt, 'R')]
    [InlineData("ctrl + shift + 5", Hotkey.Ctrl | Hotkey.Shift, '5')]
    [InlineData("Win+Alt+m", Hotkey.Win | Hotkey.Alt, 'M')]
    [InlineData("Control+Alt+F9", Hotkey.Ctrl | Hotkey.Alt, 0x78)]
    [InlineData("Alt+F24", Hotkey.Alt, 0x87)]
    public void Reads_a_shortcut(string text, uint modifiers, int key)
    {
        var hotkey = Hotkey.Parse(text);
        Assert.NotNull(hotkey);
        Assert.Equal(modifiers, hotkey.Modifiers);
        Assert.Equal((uint)key, hotkey.Key);
    }

    [Fact]
    public void Names_the_keys_the_usual_way() =>
        Assert.Equal("Ctrl+Alt+R", Hotkey.Parse("alt+ctrl+r")!.Text);

    [Theory]
    [InlineData("R")]              // no modifier: it would take the key from typing
    [InlineData("Shift+R")]        // the same, for capitals
    [InlineData("Ctrl+Alt")]       // no key
    [InlineData("Ctrl+R+T")]       // two keys
    [InlineData("Ctrl+Alt+Space")] // keys we do not name
    [InlineData("Ctrl+Alt+F25")]
    [InlineData("Hyper+R")]
    [InlineData("Ctrl++R")]
    public void Refuses_what_it_cannot_register(string text) => Assert.Null(Hotkey.Parse(text));

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void A_blank_setting_means_no_shortcut(string text) => Assert.True(Hotkey.IsOff(text));

    [Fact]
    public void A_taken_shortcut_says_what_to_do() =>
        Assert.Equal("Ctrl+Alt+R is already used by another program, so it won't start or stop recording. " +
            "Choose another shortcut in config.json (\"shortcut\"), then restart Sakshi.",
            TrayApp.ShortcutTaken(Hotkey.Parse("Ctrl+Alt+R")!));

    [Fact]
    public void An_unreadable_shortcut_shows_the_form_to_use() =>
        Assert.Contains("Use a form like Ctrl+Alt+R", TrayApp.ShortcutUnreadable("Ctrl+Space"));
}
