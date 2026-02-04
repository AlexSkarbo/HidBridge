namespace HidControl.Application.Models;

/// <summary>
/// Result of a keyboard press operation.
/// </summary>
/// <param name="Ok">True on success.</param>
/// <param name="Error">Error code/message on failure.</param>
/// <param name="ItfSel">Resolved keyboard interface selector.</param>
public sealed record KeyboardPressResult(bool Ok, string? Error, byte? ItfSel);

