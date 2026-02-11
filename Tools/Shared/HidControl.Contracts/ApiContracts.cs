using System.Text.Json;

namespace HidControl.Contracts;

// Step 1: Shared API contracts used by server and all client SDKs/apps.
/// <summary>
/// API contract model for KeyboardSnapshot.
/// </summary>
/// <param name="Modifiers">Modifiers.</param>
/// <param name="Keys">Keys.</param>
public sealed record KeyboardSnapshot(byte Modifiers, byte[] Keys);

/// <summary>
/// API contract model for MouseMoveRequest.
/// </summary>
/// <param name="Dx">Dx.</param>
/// <param name="Dy">Dy.</param>
/// <param name="Wheel">Wheel.</param>
/// <param name="ItfSel">ItfSel.</param>
public sealed record MouseMoveRequest(int Dx, int Dy, int? Wheel, byte? ItfSel);

/// <summary>
/// API contract model for MouseWheelRequest.
/// </summary>
/// <param name="Delta">Delta.</param>
/// <param name="ItfSel">ItfSel.</param>
public sealed record MouseWheelRequest(int Delta, byte? ItfSel);

/// <summary>
/// API contract model for MouseButtonRequest.
/// </summary>
/// <param name="Button">Button.</param>
/// <param name="Down">Down.</param>
/// <param name="ItfSel">ItfSel.</param>
public sealed record MouseButtonRequest(string Button, bool Down, byte? ItfSel);

/// <summary>
/// API contract model for MouseButtonsMaskRequest.
/// </summary>
/// <param name="ButtonsMask">ButtonsMask.</param>
/// <param name="ItfSel">ItfSel.</param>
public sealed record MouseButtonsMaskRequest(byte ButtonsMask, byte? ItfSel);

/// <summary>
/// API contract model for KeyboardPressRequest.
/// </summary>
/// <param name="Usage">Usage.</param>
/// <param name="Modifiers">Modifiers.</param>
/// <param name="ItfSel">ItfSel.</param>
public sealed record KeyboardPressRequest(byte Usage, byte? Modifiers, byte? ItfSel);

/// <summary>
/// API contract model for KeyboardTypeRequest.
/// </summary>
/// <param name="Text">Text.</param>
/// <param name="ItfSel">ItfSel.</param>
public sealed record KeyboardTypeRequest(string Text, byte? ItfSel);

/// <summary>
/// API contract model for KeyboardShortcutRequest.
/// </summary>
/// <param name="Shortcut">Shortcut chord (e.g. "Ctrl+Alt+Del", "Win+R", "Alt+F4").</param>
/// <param name="ItfSel">ItfSel (optional).</param>
/// <param name="HoldMs">Optional delay in milliseconds between key-down and key-up (default: server-defined).</param>
/// <param name="ApplyMapping">True to apply per-device mapping (default: true).</param>
public sealed record KeyboardShortcutRequest(string Shortcut, byte? ItfSel, int? HoldMs, bool? ApplyMapping);

/// <summary>
/// API contract model for KeyboardReportRequest.
/// </summary>
/// <param name="Modifiers">Modifiers.</param>
/// <param name="Keys">Keys.</param>
/// <param name="ItfSel">ItfSel.</param>
/// <param name="ApplyMapping">ApplyMapping.</param>
public sealed record KeyboardReportRequest(byte? Modifiers, int[]? Keys, byte? ItfSel, bool? ApplyMapping);

/// <summary>
/// API contract model for KeyboardResetRequest.
/// </summary>
/// <param name="ItfSel">ItfSel.</param>
public sealed record KeyboardResetRequest(byte? ItfSel);

/// <summary>
/// API contract model for KeyboardMappingRequest.
/// </summary>
/// <param name="DeviceId">DeviceId.</param>
/// <param name="Itf">Itf.</param>
/// <param name="ReportDescHash">ReportDescHash.</param>
/// <param name="Mapping">Mapping.</param>
public sealed record KeyboardMappingRequest(
    string DeviceId,
    byte Itf,
    string ReportDescHash,
    JsonElement Mapping);

/// <summary>
/// API contract model for RawInjectRequest.
/// </summary>
/// <param name="ItfSel">ItfSel.</param>
/// <param name="ReportHex">ReportHex.</param>
public sealed record RawInjectRequest(byte ItfSel, string? ReportHex);

/// <summary>
/// API contract model for LogLevelRequest.
/// </summary>
/// <param name="Level">Level.</param>
public sealed record LogLevelRequest(byte Level);

/// <summary>
/// API contract model for MouseMappingRequest.
/// </summary>
/// <param name="DeviceId">DeviceId.</param>
/// <param name="Itf">Itf.</param>
/// <param name="ReportDescHash">ReportDescHash.</param>
/// <param name="ButtonsCount">ButtonsCount.</param>
/// <param name="Mapping">Mapping.</param>
public sealed record MouseMappingRequest(
    string DeviceId,
    int Itf,
    string ReportDescHash,
    int ButtonsCount,
    JsonElement Mapping);

/// <summary>
/// API contract model for VideoSourceConfig.
/// </summary>
/// <param name="Id">Id.</param>
/// <param name="Kind">Kind.</param>
/// <param name="Url">Url.</param>
/// <param name="Name">Name.</param>
/// <param name="Enabled">Enabled.</param>
/// <param name="FfmpegInputOverride">FfmpegInputOverride.</param>
public sealed record VideoSourceConfig(
    string Id,
    string Kind,
    string Url,
    string? Name,
    bool Enabled,
    string? FfmpegInputOverride = null);

/// <summary>
/// API contract model for VideoSourcesRequest.
/// </summary>
/// <param name="Sources">Sources.</param>
public sealed record VideoSourcesRequest(List<VideoSourceConfig> Sources);

/// <summary>
/// API contract model for VideoProfileConfig.
/// </summary>
/// <param name="Name">Name.</param>
/// <param name="Args">Args.</param>
/// <param name="Note">Note.</param>
public sealed record VideoProfileConfig(
    string Name,
    string Args,
    string? Note);

/// <summary>
/// Video capture mode (resolution and maximum frame rate).
/// </summary>
/// <param name="Width">Frame width in pixels.</param>
/// <param name="Height">Frame height in pixels.</param>
/// <param name="MaxFps">Maximum supported frames per second.</param>
/// <param name="Format">Optional pixel format or codec (e.g. mjpeg, yuyv422, h264).</param>
public sealed record VideoMode(int Width, int Height, double MaxFps, string? Format = null);

/// <summary>
/// Result set of available video modes and MJPEG capability.
/// </summary>
/// <param name="Modes">Supported capture modes.</param>
/// <param name="SupportsMjpeg">True if the device reports MJPEG support.</param>
public sealed record VideoModesResult(IReadOnlyList<VideoMode> Modes, bool SupportsMjpeg);

/// <summary>
/// API contract model for VideoProfilesRequest.
/// </summary>
/// <param name="Profiles">Profiles.</param>
/// <param name="Active">Active.</param>
public sealed record VideoProfilesRequest(List<VideoProfileConfig> Profiles, string? Active);

/// <summary>
/// API contract model for FfmpegStartRequest.
/// </summary>
/// <param name="SourceId">SourceId.</param>
/// <param name="Restart">Restart.</param>
/// <param name="ManualStart">ManualStart.</param>
public sealed record FfmpegStartRequest(string? SourceId, bool? Restart, bool? ManualStart);

/// <summary>
/// API contract model for VideoProfileActiveRequest.
/// </summary>
/// <param name="Name">Name.</param>
public sealed record VideoProfileActiveRequest(string Name);

/// <summary>
/// API contract model for VideoOutputRequest.
/// </summary>
/// <param name="Mode">Mode.</param>
/// <param name="Hls">Hls.</param>
/// <param name="Mjpeg">Mjpeg.</param>
/// <param name="Flv">Flv.</param>
/// <param name="MjpegPassthrough">MjpegPassthrough.</param>
/// <param name="MjpegFps">MjpegFps.</param>
/// <param name="MjpegSize">MjpegSize.</param>
public sealed record VideoOutputRequest(
    string? Mode,
    bool? Hls,
    bool? Mjpeg,
    bool? Flv,
    bool? MjpegPassthrough,
    int? MjpegFps,
    string? MjpegSize);

/// <summary>
/// API contract model for VideoDshowDevice.
/// </summary>
/// <param name="Name">Name.</param>
/// <param name="AlternativeName">AlternativeName.</param>
public sealed record VideoDshowDevice(string Name, string? AlternativeName);

/// <summary>
/// API contract model for SerialPortInfo.
/// </summary>
/// <param name="Port">Port.</param>
/// <param name="Name">Name.</param>
/// <param name="DeviceId">DeviceId.</param>
/// <param name="PnpDeviceId">PnpDeviceId.</param>
/// <param name="Service">Service.</param>
public sealed record SerialPortInfo(string Port, string? Name, string? DeviceId, string? PnpDeviceId, string? Service);
