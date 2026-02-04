using System.Collections.Generic;

namespace HidControl.Application.Models;

/// <summary>
/// Result of stopping video capture workers and setting manual stop flags.
/// </summary>
/// <param name="Ok">True on success.</param>
/// <param name="Error">Error code/message on failure.</param>
/// <param name="Stopped">Stopped capture worker ids.</param>
public sealed record VideoCaptureStopResult(bool Ok, string? Error, IReadOnlyList<string> Stopped);
