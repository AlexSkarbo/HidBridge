namespace HidBridge.Hid;

/// <summary>
/// Describes one HID interface reported by the bridge firmware.
/// </summary>
public sealed record HidInterfaceInfo(
    byte DevAddr,
    byte Itf,
    byte ItfProtocol,
    byte Protocol,
    byte InferredType,
    bool Active,
    bool Mounted);

/// <summary>
/// Represents the HID interface inventory returned by the bridge.
/// </summary>
public sealed record HidInterfaceList(
    DateTimeOffset CapturedAt,
    IReadOnlyList<HidInterfaceInfo> Interfaces);

/// <summary>
/// Carries a raw report descriptor snapshot for one HID interface.
/// </summary>
public sealed record ReportDescriptorSnapshot(
    DateTimeOffset CapturedAt,
    byte Itf,
    int TotalLength,
    bool Truncated,
    byte[] Data);

/// <summary>
/// Describes a parsed mouse report layout.
/// </summary>
public sealed record MouseLayoutInfo(
    byte ReportId,
    byte ButtonsOffsetBits,
    byte ButtonsCount,
    byte ButtonsSizeBits,
    byte XOffsetBits,
    byte XSizeBits,
    bool XSigned,
    byte YOffsetBits,
    byte YSizeBits,
    bool YSigned,
    byte WheelOffsetBits,
    byte WheelSizeBits,
    bool WheelSigned,
    byte Flags);

/// <summary>
/// Describes a parsed keyboard report layout.
/// </summary>
public sealed record KeyboardLayoutInfo(
    byte ReportId,
    byte ReportLen,
    bool HasReportId)
{
    /// <summary>
    /// Builds a keyboard report that matches the parsed layout.
    /// </summary>
    /// <param name="modifiers">The HID modifier bitmask.</param>
    /// <param name="keys">The key usages to place into the report body.</param>
    /// <returns>A serialized keyboard input report.</returns>
    public byte[] Build(byte modifiers, IReadOnlyList<byte> keys)
    {
        var len = ReportLen > 0 ? ReportLen : (byte)8;
        var report = new byte[len];
        var offset = 0;
        if (HasReportId)
        {
            report[0] = ReportId;
            offset = 1;
        }

        if (offset < report.Length) report[offset] = modifiers;
        if (offset + 1 < report.Length) report[offset + 1] = 0;

        var maxKeys = Math.Min(6, Math.Max(0, report.Length - offset - 2));
        for (var i = 0; i < keys.Count && i < maxKeys; i++)
        {
            report[offset + 2 + i] = keys[i];
        }

        return report;
    }
}

/// <summary>
/// Aggregates the parsed layout metadata for one interface and report identifier.
/// </summary>
public sealed record ReportLayoutSnapshot(
    DateTimeOffset CapturedAt,
    byte Itf,
    byte ReportId,
    byte LayoutKind,
    byte Flags,
    MouseLayoutInfo? Mouse,
    KeyboardLayoutInfo? Keyboard);
