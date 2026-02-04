namespace HidControl.Application.Models;

/// <summary>
/// Result of sending a raw keyboard report (modifiers + up to 6 keys).
/// </summary>
/// <param name="Ok">True on success.</param>
/// <param name="Error">Error code/message on failure.</param>
/// <param name="ItfSel">Resolved keyboard interface selector.</param>
/// <param name="Modifiers">Final modifiers bitmask used for the report.</param>
/// <param name="Keys">Final key usages used for the report.</param>
public sealed record KeyboardReportResult(bool Ok, string? Error, byte? ItfSel, byte Modifiers, byte[] Keys);

