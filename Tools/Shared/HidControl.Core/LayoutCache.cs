using System.Collections.Concurrent;

namespace HidControl.Core;

// In-memory cache for parsed mouse layouts.
/// <summary>
/// Core model for LayoutCache.
/// </summary>
public static class LayoutCache
{
    public static ConcurrentDictionary<byte, MouseLayoutCacheEntry> MouseLayouts { get; } = new();
}

/// <summary>
/// Core model for MouseLayoutCacheEntry.
/// </summary>
/// <param name="CapturedAt">CapturedAt.</param>
/// <param name="Layout">Layout.</param>
public sealed record MouseLayoutCacheEntry(DateTimeOffset CapturedAt, MouseReportLayout Layout);
