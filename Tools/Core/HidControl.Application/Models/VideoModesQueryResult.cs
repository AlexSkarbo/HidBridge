using HidControl.Contracts;
using System.Collections.Generic;

namespace HidControl.Application.Models;

/// <summary>
/// Result of probing available video capture modes for a source.
/// </summary>
/// <param name="Ok">True on success.</param>
/// <param name="Error">Error code/message on failure.</param>
/// <param name="Cached">True if result was served from cache.</param>
/// <param name="Source">Source id.</param>
/// <param name="Device">Resolved device name used for probing.</param>
/// <param name="Modes">Available capture modes.</param>
/// <param name="SupportsMjpeg">True if device reports MJPEG support.</param>
public sealed record VideoModesQueryResult(
    bool Ok,
    string? Error,
    bool Cached,
    string? Source,
    string? Device,
    IReadOnlyList<VideoMode> Modes,
    bool SupportsMjpeg);

