namespace HidControl.Application.Models;

/// <summary>
/// Result of sending a keyboard shortcut chord.
/// </summary>
/// <param name="Ok">True on success.</param>
/// <param name="Error">Error code/message on failure.</param>
/// <param name="ItfSel">Resolved keyboard interface selector.</param>
/// <param name="Modifiers">Final modifier bitmask.</param>
/// <param name="Keys">Final pressed keys (max 6).</param>
public sealed record KeyboardShortcutResult(bool Ok, string? Error, byte? ItfSel, byte Modifiers, IReadOnlyList<byte> Keys);

