namespace HidControl.Core;

// Shared HID snapshot and layout models for server/client use.
/// <summary>
/// Core model for HidInterfaceInfo.
/// </summary>
/// <param name="DevAddr">DevAddr.</param>
/// <param name="Itf">Itf.</param>
/// <param name="ItfProtocol">ItfProtocol.</param>
/// <param name="Protocol">Protocol.</param>
/// <param name="InferredType">InferredType.</param>
/// <param name="Active">Active.</param>
/// <param name="Mounted">Mounted.</param>
public sealed record HidInterfaceInfo(
    byte DevAddr,
    byte Itf,
    byte ItfProtocol,
    byte Protocol,
    byte InferredType,
    bool Active,
    bool Mounted);

/// <summary>
/// Core model for HidInterfaceList.
/// </summary>
/// <param name="CapturedAt">CapturedAt.</param>
/// <param name="Interfaces">Interfaces.</param>
public sealed record HidInterfaceList(
    DateTimeOffset CapturedAt,
    IReadOnlyList<HidInterfaceInfo> Interfaces);

/// <summary>
/// Core model for ReportDescriptorSnapshot.
/// </summary>
/// <param name="CapturedAt">CapturedAt.</param>
/// <param name="Itf">Itf.</param>
/// <param name="TotalLength">TotalLength.</param>
/// <param name="Truncated">Truncated.</param>
/// <param name="Data">Data.</param>
public sealed record ReportDescriptorSnapshot(
    DateTimeOffset CapturedAt,
    byte Itf,
    int TotalLength,
    bool Truncated,
    byte[] Data);

/// <summary>
/// Core model for MouseLayoutInfo.
/// </summary>
/// <param name="ReportId">ReportId.</param>
/// <param name="ButtonsOffsetBits">ButtonsOffsetBits.</param>
/// <param name="ButtonsCount">ButtonsCount.</param>
/// <param name="ButtonsSizeBits">ButtonsSizeBits.</param>
/// <param name="XOffsetBits">XOffsetBits.</param>
/// <param name="XSizeBits">XSizeBits.</param>
/// <param name="XSigned">XSigned.</param>
/// <param name="YOffsetBits">YOffsetBits.</param>
/// <param name="YSizeBits">YSizeBits.</param>
/// <param name="YSigned">YSigned.</param>
/// <param name="WheelOffsetBits">WheelOffsetBits.</param>
/// <param name="WheelSizeBits">WheelSizeBits.</param>
/// <param name="WheelSigned">WheelSigned.</param>
/// <param name="Flags">Flags.</param>
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
/// Core model for KeyboardLayoutInfo.
/// </summary>
/// <param name="ReportId">ReportId.</param>
/// <param name="ReportLen">ReportLen.</param>
/// <param name="HasReportId">HasReportId.</param>
public sealed partial record KeyboardLayoutInfo(
    byte ReportId,
    byte ReportLen,
    bool HasReportId)
{
    /// <summary>
    /// Tries to parse.
    /// </summary>
    /// <param name="desc">The desc.</param>
    /// <param name="layout">The layout.</param>
    /// <returns>Result.</returns>
    public static bool TryParse(ReadOnlySpan<byte> desc, out KeyboardLayoutInfo layout)
    {
        layout = default!;
        return false;
    }

    /// <summary>
    /// Builds build.
    /// </summary>
    /// <param name="modifiers">The modifiers.</param>
    /// <param name="keys">The keys.</param>
    /// <returns>Result.</returns>
    public byte[] Build(byte modifiers, IReadOnlyList<byte> keys)
    {
        int len = ReportLen > 0 ? ReportLen : 8;
        byte[] report = new byte[len];
        int offset = 0;
        if (HasReportId)
        {
            report[0] = ReportId;
            offset = 1;
        }
        if (offset < len) report[offset] = modifiers;
        if (offset + 1 < len) report[offset + 1] = 0;
        int maxKeys = Math.Min(6, Math.Max(0, len - offset - 2));
        for (int i = 0; i < keys.Count && i < maxKeys; i++)
        {
            report[offset + 2 + i] = keys[i];
        }
        return report;
    }

    /// <summary>
    /// Builds build.
    /// </summary>
    /// <param name="layout">The layout.</param>
    /// <param name="modifiers">The modifiers.</param>
    /// <param name="keys">The keys.</param>
    /// <returns>Result.</returns>
    public static byte[] Build(KeyboardLayoutInfo layout, byte modifiers, IReadOnlyList<byte> keys)
    {
        return layout.Build(modifiers, keys);
    }
}

/// <summary>
/// Core model for ReportLayoutSnapshot.
/// </summary>
/// <param name="CapturedAt">CapturedAt.</param>
/// <param name="Itf">Itf.</param>
/// <param name="ReportId">ReportId.</param>
/// <param name="LayoutKind">LayoutKind.</param>
/// <param name="Flags">Flags.</param>
/// <param name="Mouse">Mouse.</param>
/// <param name="Keyboard">Keyboard.</param>
public sealed record ReportLayoutSnapshot(
    DateTimeOffset CapturedAt,
    byte Itf,
    byte ReportId,
    byte LayoutKind,
    byte Flags,
    MouseLayoutInfo? Mouse,
    KeyboardLayoutInfo? Keyboard);
