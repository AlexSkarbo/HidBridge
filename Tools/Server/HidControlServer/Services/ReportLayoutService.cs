using System;
using HidControl.Core;
using HidControlServer;

namespace HidControlServer.Services;

/// <summary>
/// Resolves report layout metadata from device snapshots.
/// </summary>
internal static class ReportLayoutService
{
    /// <summary>
    /// Tries to resolve the mouse report layout for the interface.
    /// </summary>
    /// <param name="uart">UART client.</param>
    /// <param name="itfSel">Interface selector.</param>
    /// <param name="layout">Resolved mouse layout.</param>
    /// <returns>True if resolved.</returns>
    public static bool TryGetMouseLayout(HidUartClient uart, byte itfSel, out MouseReportLayout layout)
    {
        layout = default!;
        return TryGetMouseLayoutFromSnapshot(uart, itfSel, out layout);
    }

    /// <summary>
    /// Tries to resolve the mouse report layout from descriptor snapshot.
    /// </summary>
    /// <param name="uart">UART client.</param>
    /// <param name="itfSel">Interface selector.</param>
    /// <param name="layout">Resolved mouse layout.</param>
    /// <returns>True if resolved.</returns>
    public static bool TryGetMouseLayoutFromSnapshot(HidUartClient uart, byte itfSel, out MouseReportLayout layout)
    {
        layout = default!;
        ReportDescriptorSnapshot? snap = uart.GetReportDescriptorCopy(itfSel);
        if (snap is null || snap.Data.Length == 0) return false;
        if (!HidReportParser.TryParseMouseLayout(snap.Data, out MouseReportLayout parsed)) return false;
        layout = parsed;
        return true;
    }

    /// <summary>
    /// Tries to resolve the keyboard report layout for the interface.
    /// </summary>
    /// <param name="uart">UART client.</param>
    /// <param name="itfSel">Interface selector.</param>
    /// <param name="layout">Resolved keyboard layout.</param>
    /// <returns>True if resolved.</returns>
    public static bool TryGetKeyboardLayout(HidUartClient uart, byte itfSel, out KeyboardLayoutInfo layout)
    {
        layout = default!;
        ReportLayoutSnapshot? snap = uart.GetReportLayoutCopy(itfSel);
        if (snap?.Keyboard is not null)
        {
            layout = snap.Keyboard;
            return true;
        }
        ReportDescriptorSnapshot? desc = uart.GetReportDescriptorCopy(itfSel);
        if (desc is null || desc.Data.Length == 0) return false;
        if (!KeyboardLayoutInfo.TryParse(desc.Data, out KeyboardLayoutInfo parsed)) return false;
        layout = parsed;
        return true;
    }

    /// <summary>
    /// Ensures the mouse report layout is available by requesting layouts.
    /// </summary>
    /// <param name="uart">UART client.</param>
    /// <param name="itfSel">Interface selector.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task EnsureMouseLayoutAsync(HidUartClient uart, byte itfSel, CancellationToken ct)
    {
        if (uart.GetReportLayoutCopy(itfSel)?.Mouse is not null) return;
        if (TryGetMouseLayout(uart, itfSel, out _)) return;
        try
        {
            await uart.RequestReportLayoutAsync(itfSel, 0, 500, ct);
            if (uart.GetReportLayoutCopy(itfSel)?.Mouse is not null) return;
            if (TryGetMouseLayout(uart, itfSel, out _)) return;
        }
        catch { }
        try
        {
            await uart.RequestReportLayoutAsync(itfSel, 0, 500, ct, true);
            if (uart.GetReportLayoutCopy(itfSel)?.Mouse is not null) return;
            if (TryGetMouseLayout(uart, itfSel, out _)) return;
        }
        catch { }
        _ = TryGetMouseLayout(uart, itfSel, out _);
    }

    /// <summary>
    /// Ensures the keyboard report layout is available by requesting layouts.
    /// </summary>
    /// <param name="uart">UART client.</param>
    /// <param name="itfSel">Interface selector.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="reportId">Report ID (if applicable).</param>
    public static async Task EnsureReportLayoutAsync(HidUartClient uart, byte itfSel, CancellationToken ct, byte reportId = 0)
    {
        if (TryGetKeyboardLayout(uart, itfSel, out _)) return;
        try
        {
            await uart.RequestReportLayoutAsync(itfSel, reportId, 500, ct);
            if (TryGetKeyboardLayout(uart, itfSel, out _)) return;
        }
        catch { }
        try
        {
            await uart.RequestReportLayoutAsync(itfSel, reportId, 500, ct, true);
            if (TryGetKeyboardLayout(uart, itfSel, out _)) return;
        }
        catch { }
    }
}
