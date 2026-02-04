namespace HidControl.Application.Models;

/// <summary>
/// Result of a mouse move operation.
/// </summary>
/// <param name="Ok">True on success.</param>
/// <param name="Error">Error code/message on failure.</param>
/// <param name="Skipped">True when the operation was skipped by policy (e.g. zero delta not allowed).</param>
/// <param name="ItfSel">Resolved mouse interface selector.</param>
public sealed record MouseMoveResult(bool Ok, string? Error, bool Skipped, byte? ItfSel);

