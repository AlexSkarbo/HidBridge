namespace HidControl.Application.Models;

/// <summary>
/// Killed orphan process info.
/// </summary>
/// <param name="SourceId">Source id.</param>
/// <param name="Pids">Killed process ids.</param>
public sealed record OrphanKillInfo(string SourceId, IReadOnlyList<int> Pids);
