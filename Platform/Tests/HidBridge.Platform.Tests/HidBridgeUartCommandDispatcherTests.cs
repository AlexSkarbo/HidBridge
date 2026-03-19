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
    [InlineData(" mouse.move ", "mouse.move")]
    [InlineData("MOUSE_BUTTON", "mouse.button")]
    [InlineData("MOUSE_CLICK", "mouse.click")]
    public void NormalizeAction_NormalizesLegacyAliases(string input, string expected)
    {
        var actual = HidBridgeUartCommandDispatcher.NormalizeAction(input);

        Assert.Equal(expected, actual);
    }
}
