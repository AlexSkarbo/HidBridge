namespace HidControl.Application.Models;

/// <summary>
/// Result of changing keyboard state (down/up/reset) and emitting a report.
/// </summary>
/// <param name="Ok">True on success.</param>
/// <param name="Error">Error code/message on failure.</param>
/// <param name="ItfSel">Resolved keyboard interface selector.</param>
/// <param name="Snapshot">Current keyboard snapshot after applying.</param>
public sealed record KeyboardStateChangeResult(bool Ok, string? Error, byte? ItfSel, HidControl.Contracts.KeyboardSnapshot? Snapshot);
