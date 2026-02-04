using HidControl.Contracts;

namespace HidControl.Application.Models;

/// <summary>
/// Result of reading current video output state.
/// </summary>
/// <param name="Ok">True on success.</param>
/// <param name="Error">Error message/code on failure.</param>
/// <param name="State">Output state.</param>
/// <param name="Mode">Resolved output mode string.</param>
public sealed record VideoOutputStatusResult(bool Ok, string? Error, VideoOutputState State, string Mode);

