namespace HidControl.Application.Models;

/// <summary>
/// Result of a mouse wheel operation.
/// </summary>
/// <param name="Ok">True on success.</param>
/// <param name="Error">Error code/message on failure.</param>
/// <param name="Skipped">True when the operation was skipped by policy.</param>
/// <param name="ItfSel">Resolved mouse interface selector.</param>
public sealed record MouseWheelResult(bool Ok, string? Error, bool Skipped, byte? ItfSel);

