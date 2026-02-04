namespace HidControl.UseCases.Video;

/// <summary>
/// Use case model for FlvHubStats.
/// </summary>
/// <param name="HasHeader">HasHeader.</param>
/// <param name="HasVideoConfig">HasVideoConfig.</param>
/// <param name="HasKeyframe">HasKeyframe.</param>
/// <param name="SubscribersActive">SubscribersActive.</param>
/// <param name="LastTagAtUtc">LastTagAtUtc.</param>
public readonly record struct FlvHubStats(
    bool HasHeader,
    bool HasVideoConfig,
    bool HasKeyframe,
    int SubscribersActive,
    DateTimeOffset LastTagAtUtc);
