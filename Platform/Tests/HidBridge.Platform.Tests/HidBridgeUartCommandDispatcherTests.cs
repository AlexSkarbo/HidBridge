using HidBridge.Transport.Uart;
using Xunit;

namespace HidBridge.Platform.Tests;

/// <summary>
/// Verifies normalization rules shared by UART HID command dispatchers.
/// </summary>
public sealed class HidBridgeUartCommandDispatcherTests
{
    [Theory]
    [InlineData("keyboard_text", "keyboard.text")]
    [InlineData("keyboard_press", "keyboard.press")]
    [InlineData("keyboard_shortcut", "keyboard.shortcut")]
    [InlineData("keyboard_reset", "keyboard.reset")]
    [InlineData(" mouse.move ", "mouse.move")]
    [InlineData("MOUSE_BUTTON", "mouse.button")]
    [InlineData("MOUSE_CLICK", "mouse.click")]
    [InlineData("mouse_wheel", "mouse.wheel")]
    [InlineData("mouse_buttons", "mouse.buttons")]
    public void NormalizeAction_NormalizesLegacyAliases(string input, string expected)
    {
        var actual = HidBridgeUartCommandDispatcher.NormalizeAction(input);

        Assert.Equal(expected, actual);
    }
}
