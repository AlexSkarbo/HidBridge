using System;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using HidControl.Core;
using HidControl.Contracts;
using HidControlServer;

namespace HidControlServer.Services;

/// <summary>
/// Builds and maps HID input reports for mouse and keyboard operations.
/// </summary>
internal static class InputReportBuilder
{
    /// <summary>
    /// Builds a mouse input report using layout metadata when available.
    /// </summary>
    /// <param name="uart">UART client.</param>
    /// <param name="itfSel">Interface selector.</param>
    /// <param name="buttons">Buttons bitmask.</param>
    /// <param name="dx">Delta X.</param>
    /// <param name="dy">Delta Y.</param>
    /// <param name="wheel">Wheel delta.</param>
    /// <param name="fallbackLen">Fallback report length.</param>
    /// <returns>Report bytes.</returns>
    public static byte[] TryBuildMouseReport(HidUartClient uart, byte itfSel, byte buttons, int dx, int dy, int wheel, int fallbackLen)
    {
        ReportLayoutSnapshot? layoutSnap = uart.GetReportLayoutCopy(itfSel);
        if (layoutSnap?.Mouse is not null)
        {
            return HidReports.BuildMouseFromLayoutInfo(layoutSnap.Mouse, buttons, dx, dy, wheel);
        }
        if (ReportLayoutService.TryGetMouseLayout(uart, itfSel, out MouseReportLayout layout))
        {
            return HidReports.BuildMouseFromLayout(layout, buttons, dx, dy, wheel);
        }
        if (fallbackLen == 3 || fallbackLen == 4)
        {
            return HidReports.BuildBootMouse(buttons, dx, dy, wheel, fallbackLen);
        }
        byte[] report = new byte[fallbackLen];
        if (fallbackLen > 0) report[0] = buttons;
        if (fallbackLen > 1) report[1] = unchecked((byte)(sbyte)dx);
        if (fallbackLen > 2) report[2] = unchecked((byte)(sbyte)dy);
        if (fallbackLen > 3) report[3] = unchecked((byte)(sbyte)wheel);
        return report;
    }

    /// <summary>
    /// Builds a keyboard input report using layout metadata when available.
    /// </summary>
    /// <param name="uart">UART client.</param>
    /// <param name="itfSel">Interface selector.</param>
    /// <param name="modifiers">Modifier bitmask.</param>
    /// <param name="keys">Pressed key usages.</param>
    /// <returns>Report bytes.</returns>
    public static byte[] TryBuildKeyboardReport(HidUartClient uart, byte itfSel, byte modifiers, IReadOnlyList<byte> keys)
    {
        if (ReportLayoutService.TryGetKeyboardLayout(uart, itfSel, out KeyboardLayoutInfo layout))
        {
            int len = layout.ReportLen > 0 ? layout.ReportLen : 8;
            byte[] report = new byte[len];
            int offset = 0;
            if (layout.HasReportId)
            {
                report[0] = layout.ReportId;
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
        return HidReports.BuildBootKeyboard(modifiers, keys);
    }

    /// <summary>
    /// Applies a mouse button mask to the current mouse state.
    /// </summary>
    /// <param name="state">Mouse state.</param>
    /// <param name="mask">Button mask.</param>
    /// <param name="down">True to press, false to release.</param>
    public static void ApplyMouseButtonMask(MouseState state, byte mask, bool down)
    {
        byte current = state.GetButtons();
        byte next = down ? (byte)(current | mask) : (byte)(current & ~mask);
        state.SetButtonsMask(next);
    }

    /// <summary>
    /// Resolves a button name to an interface-specific mask.
    /// </summary>
    /// <param name="button">Button name.</param>
    /// <param name="opt">Options.</param>
    /// <param name="uart">UART client.</param>
    /// <param name="store">Mouse mapping store.</param>
    /// <param name="itfSel">Interface selector.</param>
    /// <returns>Button mask.</returns>
    public static byte ResolveMouseButtonMask(string button, Options opt, HidUartClient uart, MouseMappingStore store, byte itfSel)
    {
        if (TryGetMappedMask(button, uart, store, itfSel, out byte mask))
        {
            return mask;
        }
        string key = button.ToLowerInvariant();
        return key switch
        {
            "left" => opt.MouseLeftMask,
            "right" => opt.MouseRightMask,
            "middle" => opt.MouseMiddleMask,
            "back" => opt.MouseBackMask,
            "forward" => opt.MouseForwardMask,
            _ => ParseButtonNMask(button)
        };
    }

    /// <summary>
    /// Parses a button index expression like "b4" into a bitmask.
    /// </summary>
    /// <param name="b">Button expression.</param>
    /// <returns>Button mask.</returns>
    public static byte ParseButtonNMask(string b)
    {
        if (!b.StartsWith("b", StringComparison.OrdinalIgnoreCase)) return 0;
        string n = b[1..];
        if (!int.TryParse(n, out int idx)) return 0;
        if (idx < 0 || idx > 7) return 0;
        return (byte)(1 << idx);
    }

    /// <summary>
    /// Tries to resolve a mapped button mask from stored mappings.
    /// </summary>
    /// <param name="button">Button name.</param>
    /// <param name="uart">UART client.</param>
    /// <param name="store">Mouse mapping store.</param>
    /// <param name="itfSel">Interface selector.</param>
    /// <param name="mask">Resolved mask.</param>
    /// <returns>True if resolved.</returns>
    public static bool TryGetMappedMask(string button, HidUartClient uart, MouseMappingStore store, byte itfSel, out byte mask)
    {
        mask = 0;
        string? deviceId = GetDeviceIdForItf(uart, itfSel);
        if (deviceId is null) return false;
        var mapping = store.Get(deviceId);
        if (mapping is null) return false;
        if (mapping.Mapping.ValueKind != JsonValueKind.Object) return false;
        if (!mapping.Mapping.TryGetProperty(button, out JsonElement value)) return false;
        if (value.ValueKind == JsonValueKind.Number)
        {
            if (!value.TryGetByte(out mask)) return false;
            return true;
        }
        if (value.ValueKind == JsonValueKind.String)
        {
            string? str = value.GetString();
            if (string.IsNullOrWhiteSpace(str)) return false;
            if (!byte.TryParse(str, NumberStyles.Integer, CultureInfo.InvariantCulture, out mask)) return false;
            return true;
        }
        return true;
    }

    /// <summary>
    /// Builds a device identifier for the specified interface, if available.
    /// </summary>
    /// <param name="uart">UART client.</param>
    /// <param name="itfSel">Interface selector.</param>
    /// <returns>Device identifier.</returns>
    public static string? GetDeviceIdForItf(HidUartClient uart, byte itfSel)
    {
        HidInterfaceList? list = uart.GetLastInterfaceList();
        if (list is null) return null;
        HidInterfaceInfo? itf = list.Interfaces.FirstOrDefault(i => i.Itf == itfSel);
        if (itf is null) return null;
        string? hash = null;
        ReportDescriptorSnapshot? desc = uart.GetReportDescriptorCopy(itfSel);
        if (desc is not null && desc.Data.Length > 0)
        {
            hash = DeviceInterfaceMapper.ComputeReportDescHash(desc.Data);
        }
        return DeviceInterfaceMapper.BuildDeviceId(itf.InferredType, itf.ItfProtocol, hash);
    }

    /// <summary>
    /// Sends a mouse button press report.
    /// </summary>
    /// <param name="state">Mouse state.</param>
    /// <param name="uart">UART client.</param>
    /// <param name="opt">Options.</param>
    /// <param name="itfSel">Interface selector.</param>
    /// <param name="mask">Button mask.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task MousePressAsync(MouseState state, HidUartClient uart, Options opt, byte itfSel, byte mask, CancellationToken ct)
    {
        ApplyMouseButtonMask(state, mask, true);
        await ReportLayoutService.EnsureMouseLayoutAsync(uart, itfSel, ct);
        byte[] report = TryBuildMouseReport(uart, itfSel, state.GetButtons(), 0, 0, 0, opt.MouseReportLen);
        await uart.SendInjectReportAsync(itfSel, report, opt.InjectTimeoutMs, opt.InjectRetries, false, ct);
    }

    /// <summary>
    /// Sends a mouse button release report.
    /// </summary>
    /// <param name="state">Mouse state.</param>
    /// <param name="uart">UART client.</param>
    /// <param name="opt">Options.</param>
    /// <param name="itfSel">Interface selector.</param>
    /// <param name="mask">Button mask.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task MouseReleaseAsync(MouseState state, HidUartClient uart, Options opt, byte itfSel, byte mask, CancellationToken ct)
    {
        ApplyMouseButtonMask(state, mask, false);
        await ReportLayoutService.EnsureMouseLayoutAsync(uart, itfSel, ct);
        byte[] report = TryBuildMouseReport(uart, itfSel, state.GetButtons(), 0, 0, 0, opt.MouseReportLen);
        await uart.SendInjectReportAsync(itfSel, report, opt.InjectTimeoutMs, opt.InjectRetries, false, ct);
    }

    /// <summary>
    /// Sends a press and release sequence for a mouse button.
    /// </summary>
    /// <param name="state">Mouse state.</param>
    /// <param name="uart">UART client.</param>
    /// <param name="opt">Options.</param>
    /// <param name="itfSel">Interface selector.</param>
    /// <param name="mask">Button mask.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task MouseClickAsync(MouseState state, HidUartClient uart, Options opt, byte itfSel, byte mask, CancellationToken ct)
    {
        await MousePressAsync(state, uart, opt, itfSel, mask, ct);
        await MouseReleaseAsync(state, uart, opt, itfSel, mask, ct);
    }

    /// <summary>
    /// Resolves a keyboard usage through mapping when available.
    /// </summary>
    /// <param name="uart">UART client.</param>
    /// <param name="store">Keyboard mapping store.</param>
    /// <param name="itfSel">Interface selector.</param>
    /// <param name="usage">Usage ID.</param>
    /// <returns>Mapped usage.</returns>
    public static byte ResolveKeyboardUsage(HidUartClient uart, KeyboardMappingStore store, byte itfSel, byte usage)
    {
        if (TryGetMappedUsage(uart, store, itfSel, usage, out byte mapped))
        {
            return mapped;
        }
        return usage;
    }

    /// <summary>
    /// Checks whether a usage is a modifier key.
    /// </summary>
    /// <param name="usage">Usage ID.</param>
    /// <returns>True if modifier.</returns>
    public static bool IsModifierUsage(byte usage) => usage >= 0xE0 && usage <= 0xE7;

    /// <summary>
    /// Computes the modifier bit for a modifier usage.
    /// </summary>
    /// <param name="usage">Usage ID.</param>
    /// <returns>Modifier bit.</returns>
    public static byte ModifierBit(byte usage) => (byte)(1 << (usage - 0xE0));

    /// <summary>
    /// Applies modifier mapping to a report's modifier and key list.
    /// </summary>
    /// <param name="uart">UART client.</param>
    /// <param name="store">Keyboard mapping store.</param>
    /// <param name="itfSel">Interface selector.</param>
    /// <param name="modifiers">Original modifiers.</param>
    /// <param name="keys">Key list.</param>
    /// <returns>Mapped modifier bitmask.</returns>
    public static byte ApplyModifierMapping(HidUartClient uart, KeyboardMappingStore store, byte itfSel, byte modifiers, List<byte> keys)
    {
        if (!TryGetMappedUsage(uart, store, itfSel, 0xE0, out _)) return modifiers;

        byte newMods = 0;
        for (int i = keys.Count - 1; i >= 0; i--)
        {
            byte usage = keys[i];
            byte mapped = ResolveKeyboardUsage(uart, store, itfSel, usage);
            if (IsModifierUsage(mapped))
            {
                newMods |= ModifierBit(mapped);
                keys.RemoveAt(i);
            }
        }
        return (byte)(modifiers | newMods);
    }

    /// <summary>
    /// Tries to map a keyboard usage using per-device mapping.
    /// </summary>
    /// <param name="uart">UART client.</param>
    /// <param name="store">Keyboard mapping store.</param>
    /// <param name="itfSel">Interface selector.</param>
    /// <param name="usage">Usage ID.</param>
    /// <param name="mapped">Mapped usage.</param>
    /// <returns>True if mapping exists.</returns>
    public static bool TryGetMappedUsage(HidUartClient uart, KeyboardMappingStore store, byte itfSel, byte usage, out byte mapped)
    {
        mapped = usage;
        string? deviceId = GetDeviceIdForItf(uart, itfSel);
        if (deviceId is null) return false;
        var mapping = store.Get(deviceId);
        if (mapping is null) return false;
        if (mapping.Mapping.ValueKind != JsonValueKind.Object) return false;
        if (!mapping.Mapping.TryGetProperty(usage.ToString(CultureInfo.InvariantCulture), out JsonElement value)) return false;
        if (value.ValueKind == JsonValueKind.Number)
        {
            if (!value.TryGetByte(out mapped)) return false;
            return true;
        }
        if (value.ValueKind == JsonValueKind.String)
        {
            string? str = value.GetString();
            if (string.IsNullOrWhiteSpace(str)) return false;
            if (!byte.TryParse(str, NumberStyles.Integer, CultureInfo.InvariantCulture, out mapped)) return false;
            return true;
        }
        return true;
    }
}
