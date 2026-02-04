namespace HidControl.Application.Models;

/// <summary>
/// Result of typing a text string via HID key presses.
/// </summary>
/// <param name="Ok">True on success.</param>
/// <param name="Error">Error code/message on failure.</param>
/// <param name="ItfSel">Resolved keyboard interface selector.</param>
/// <param name="UnsupportedChar">First unsupported character code point, if any.</param>
public sealed record KeyboardTextResult(bool Ok, string? Error, byte? ItfSel, int? UnsupportedChar);

