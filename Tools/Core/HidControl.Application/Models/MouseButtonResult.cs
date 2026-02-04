namespace HidControl.Application.Models;

/// <summary>
/// Result of setting a mouse button state.
/// </summary>
/// <param name="Ok">True on success.</param>
/// <param name="Error">Error code/message on failure.</param>
/// <param name="ItfSel">Resolved mouse interface selector.</param>
/// <param name="ButtonsMask">Current buttons mask after applying.</param>
public sealed record MouseButtonResult(bool Ok, string? Error, byte? ItfSel, byte ButtonsMask);

