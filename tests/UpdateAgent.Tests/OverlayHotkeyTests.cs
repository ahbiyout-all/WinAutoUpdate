using UpdateAgent.SessionHelper;

namespace UpdateAgent.Tests;

public sealed class OverlayHotkeyTests
{
    [Theory]
    [InlineData(KeyboardBlocker.VkLWin, false, false, false, true, true)]
    [InlineData(KeyboardBlocker.VkRWin, false, false, false, true, true)]
    [InlineData(KeyboardBlocker.VkF4, true, false, false, false, true)]
    [InlineData(KeyboardBlocker.VkTab, true, false, false, false, true)]
    [InlineData(KeyboardBlocker.VkEscape, false, true, false, false, true)]
    [InlineData(KeyboardBlocker.VkEscape, false, true, true, false, true)]
    [InlineData(KeyboardBlocker.VkD, false, false, false, true, true)]
    [InlineData(KeyboardBlocker.VkM, false, false, false, true, true)]
    [InlineData(KeyboardBlocker.VkD, false, false, false, false, false)]
    public void ShouldBlockKey_SuppressesSystemShortcutsOnly(
        int vk,
        bool alt,
        bool ctrl,
        bool shift,
        bool win,
        bool expected)
    {
        Assert.Equal(expected, KeyboardBlocker.ShouldBlockKey(vk, alt, ctrl, shift, win));
    }
}
