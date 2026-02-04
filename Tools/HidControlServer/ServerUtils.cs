using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using HidControl.Core;
using HidControlServer.Services;

namespace HidControlServer;

// Step 2: ServerUtils is a compatibility facade. New code should use services directly.
/// <summary>
/// Compatibility facade for server utility helpers.
/// </summary>
internal static partial class ServerUtils
{
    /// <summary>
    /// Builds a device list response from stored keyboard and mouse mappings.
    /// </summary>
    public static object BuildDevicesFromMappings(MouseMappingStore mouseStore, KeyboardMappingStore keyboardStore)
        => DeviceInterfaceMapper.BuildDevicesFromMappings(mouseStore, keyboardStore);

    /// <summary>
    /// Maps an interface list to a lightweight response payload.
    /// </summary>
    public static object MapInterfaceList(HidInterfaceList list)
        => DeviceInterfaceMapper.MapInterfaceList(list);

    /// <summary>
    /// Maps an interface list with report descriptors and layout details.
    /// </summary>
    public static object MapInterfaceListDetailed(HidInterfaceList list, HidUartClient? uart, bool includeReportDesc)
        => DeviceInterfaceMapper.MapInterfaceListDetailed(list, uart, includeReportDesc);

    /// <summary>
    /// Infers the device type name from interface metadata.
    /// </summary>
    public static string InferTypeName(byte inferredType, byte itfProtocol)
        => DeviceInterfaceMapper.InferTypeName(inferredType, itfProtocol);

    /// <summary>
    /// Builds a stable device identifier from type and descriptor hash.
    /// </summary>
    public static string? BuildDeviceId(byte inferredType, byte itfProtocol, string? reportDescHash)
        => DeviceInterfaceMapper.BuildDeviceId(inferredType, itfProtocol, reportDescHash);

    /// <summary>
    /// Computes the SHA-256 hash of a report descriptor.
    /// </summary>
    public static string ComputeReportDescHash(byte[] data)
        => DeviceInterfaceMapper.ComputeReportDescHash(data);

    /// <summary>
    /// Resolves the interface selector based on request, type, and fallback.
    /// </summary>
    public static byte ResolveItfSel(byte? requestItfSel, string? typeName, byte fallbackItfSel, HidUartClient uart)
        => InterfaceSelector.ResolveItfSel(requestItfSel, typeName, fallbackItfSel, uart);

    /// <summary>
    /// Resolves the interface selector and refreshes interface list if needed.
    /// </summary>
    public static Task<byte> ResolveItfSelAsync(byte? requestItfSel, string? typeName, byte fallbackItfSel, HidUartClient uart, CancellationToken ct)
        => InterfaceSelector.ResolveItfSelAsync(requestItfSel, typeName, fallbackItfSel, uart, ct);

    /// <summary>
    /// Tries to resolve an interface selector from the cached devices document.
    /// </summary>
    public static bool TryResolveItfSelFromCache(AppState state, string typeName, out byte itfSel)
        => InterfaceSelector.TryResolveItfSelFromCache(state, typeName, out itfSel);

    /// <summary>
    /// Returns active interface IDs from the last interface list snapshot.
    /// </summary>
    public static IReadOnlyList<byte> GetActiveItfs(HidUartClient uart)
        => InterfaceSelector.GetActiveItfs(uart);

    /// <summary>
    /// Builds a mouse input report using layout metadata when available.
    /// </summary>
    public static byte[] TryBuildMouseReport(HidUartClient uart, byte itfSel, byte buttons, int dx, int dy, int wheel, int fallbackLen)
        => InputReportBuilder.TryBuildMouseReport(uart, itfSel, buttons, dx, dy, wheel, fallbackLen);

    /// <summary>
    /// Builds a keyboard input report using layout metadata when available.
    /// </summary>
    public static byte[] TryBuildKeyboardReport(HidUartClient uart, byte itfSel, byte modifiers, IReadOnlyList<byte> keys)
        => InputReportBuilder.TryBuildKeyboardReport(uart, itfSel, modifiers, keys);

    /// <summary>
    /// Applies a mouse button mask to the current mouse state.
    /// </summary>
    public static void ApplyMouseButtonMask(MouseState state, byte mask, bool down)
        => InputReportBuilder.ApplyMouseButtonMask(state, mask, down);

    /// <summary>
    /// Resolves a button name to an interface-specific mask.
    /// </summary>
    public static byte ResolveMouseButtonMask(string button, Options opt, HidUartClient uart, MouseMappingStore store, byte itfSel)
        => InputReportBuilder.ResolveMouseButtonMask(button, opt, uart, store, itfSel);

    /// <summary>
    /// Parses a button index expression like "b4" into a bitmask.
    /// </summary>
    public static byte ParseButtonNMask(string b)
        => InputReportBuilder.ParseButtonNMask(b);

    /// <summary>
    /// Tries to resolve a mapped button mask from stored mappings.
    /// </summary>
    public static bool TryGetMappedMask(string button, HidUartClient uart, MouseMappingStore store, byte itfSel, out byte mask)
        => InputReportBuilder.TryGetMappedMask(button, uart, store, itfSel, out mask);

    /// <summary>
    /// Builds a device identifier for the specified interface, if available.
    /// </summary>
    public static string? GetDeviceIdForItf(HidUartClient uart, byte itfSel)
        => InputReportBuilder.GetDeviceIdForItf(uart, itfSel);

    /// <summary>
    /// Sends a mouse button press report.
    /// </summary>
    public static Task MousePressAsync(MouseState state, HidUartClient uart, Options opt, byte itfSel, byte mask, CancellationToken ct)
        => InputReportBuilder.MousePressAsync(state, uart, opt, itfSel, mask, ct);

    /// <summary>
    /// Sends a mouse button release report.
    /// </summary>
    public static Task MouseReleaseAsync(MouseState state, HidUartClient uart, Options opt, byte itfSel, byte mask, CancellationToken ct)
        => InputReportBuilder.MouseReleaseAsync(state, uart, opt, itfSel, mask, ct);

    /// <summary>
    /// Sends a press and release sequence for a mouse button.
    /// </summary>
    public static Task MouseClickAsync(MouseState state, HidUartClient uart, Options opt, byte itfSel, byte mask, CancellationToken ct)
        => InputReportBuilder.MouseClickAsync(state, uart, opt, itfSel, mask, ct);

    /// <summary>
    /// Tries to resolve the mouse report layout for the interface.
    /// </summary>
    public static bool TryGetMouseLayout(HidUartClient uart, byte itfSel, out MouseReportLayout layout)
        => ReportLayoutService.TryGetMouseLayout(uart, itfSel, out layout);

    /// <summary>
    /// Tries to resolve the mouse report layout from descriptor snapshot.
    /// </summary>
    public static bool TryGetMouseLayoutFromSnapshot(HidUartClient uart, byte itfSel, out MouseReportLayout layout)
        => ReportLayoutService.TryGetMouseLayoutFromSnapshot(uart, itfSel, out layout);

    /// <summary>
    /// Tries to resolve the keyboard report layout for the interface.
    /// </summary>
    public static bool TryGetKeyboardLayout(HidUartClient uart, byte itfSel, out KeyboardLayoutInfo layout)
        => ReportLayoutService.TryGetKeyboardLayout(uart, itfSel, out layout);

    /// <summary>
    /// Ensures the mouse report layout is available by requesting layouts.
    /// </summary>
    public static Task EnsureMouseLayoutAsync(HidUartClient uart, byte itfSel, CancellationToken ct)
        => ReportLayoutService.EnsureMouseLayoutAsync(uart, itfSel, ct);

    /// <summary>
    /// Ensures the keyboard report layout is available by requesting layouts.
    /// </summary>
    public static Task EnsureReportLayoutAsync(HidUartClient uart, byte itfSel, CancellationToken ct, byte reportId = 0)
        => ReportLayoutService.EnsureReportLayoutAsync(uart, itfSel, ct, reportId);

    /// <summary>
    /// Checks whether an exception looks like an injection failure.
    /// </summary>
    public static bool IsInjectFailed(Exception ex)
        => DeviceDiagnostics.IsInjectFailed(ex);

    /// <summary>
    /// Gets the bootstrap HMAC key from options.
    /// </summary>
    public static string GetBootstrapKey(Options opt)
        => DeviceKeyService.GetBootstrapKey(opt);

    /// <summary>
    /// Initializes the device key and logs any failures.
    /// </summary>
    public static Task InitializeDeviceKeyAsync(Options opt, HidUartClient uart, CancellationToken ct)
        => DeviceKeyService.InitializeDeviceKeyAsync(opt, uart, ct);

    /// <summary>
    /// Computes a derived HMAC key from master secret and device ID.
    /// </summary>
    public static byte[] ComputeDerivedKey(string masterSecret, byte[] deviceId)
        => DeviceKeyService.ComputeDerivedKey(masterSecret, deviceId);

    /// <summary>
    /// Ensures a derived HMAC key is installed on the device.
    /// </summary>
    public static Task EnsureDeviceKeyAsync(Options opt, HidUartClient uart, CancellationToken ct)
        => DeviceKeyService.EnsureDeviceKeyAsync(opt, uart, ct);

    /// <summary>
    /// Tries to resolve the repository version from assembly metadata.
    /// </summary>
    public static string? FindRepoVersion()
        => DeviceDiagnostics.FindRepoVersion();

    /// <summary>
    /// Resolves a keyboard usage through mapping when available.
    /// </summary>
    public static byte ResolveKeyboardUsage(HidUartClient uart, KeyboardMappingStore store, byte itfSel, byte usage)
        => InputReportBuilder.ResolveKeyboardUsage(uart, store, itfSel, usage);

    /// <summary>
    /// Checks whether a usage is a modifier key.
    /// </summary>
    public static bool IsModifierUsage(byte usage)
        => InputReportBuilder.IsModifierUsage(usage);

    /// <summary>
    /// Computes the modifier bit for a modifier usage.
    /// </summary>
    public static byte ModifierBit(byte usage)
        => InputReportBuilder.ModifierBit(usage);

    /// <summary>
    /// Applies modifier mapping to a report's modifier and key list.
    /// </summary>
    public static byte ApplyModifierMapping(HidUartClient uart, KeyboardMappingStore store, byte itfSel, byte modifiers, List<byte> keys)
        => InputReportBuilder.ApplyModifierMapping(uart, store, itfSel, modifiers, keys);

    /// <summary>
    /// Tries to map a keyboard usage using per-device mapping.
    /// </summary>
    public static bool TryGetMappedUsage(HidUartClient uart, KeyboardMappingStore store, byte itfSel, byte usage, out byte mapped)
        => InputReportBuilder.TryGetMappedUsage(uart, store, itfSel, usage, out mapped);

    /// <summary>
    /// Background loop to refresh interface list when auto-refresh is enabled.
    /// </summary>
    public static Task AutoRefreshDevicesLoop(Options opt, HidUartClient uart, CancellationToken ct)
        => DeviceDiagnostics.AutoRefreshDevicesLoop(opt, uart, ct);
}
