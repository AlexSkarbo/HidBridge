namespace HidControl.Application.Models;

/// <summary>
/// Result of setting a mouse button mask.
/// </summary>
/// <param name="Ok">True on success.</param>
/// <param name="Error">Error code/message on failure.</param>
/// <param name="ItfSel">Resolved mouse interface selector.</param>
/// <param name="ButtonsMask">Applied buttons mask.</param>
public sealed record MouseButtonsMaskResult(bool Ok, string? Error, byte? ItfSel, byte ButtonsMask);

