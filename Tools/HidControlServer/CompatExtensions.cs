using System.Text.Json;
using HidControlServer.Services;

namespace HidControlServer;

/// <summary>
/// Compatibility extension helpers.
/// </summary>
public static class CompatExtensions
{
    /// <summary>
    /// Tries to get value.
    /// </summary>
    /// <param name="element">The element.</param>
    /// <param name="propertyName">The propertyName.</param>
    /// <param name="value">The value.</param>
    /// <returns>Result.</returns>
    public static bool TryGetValue(this JsonElement element, string propertyName, out JsonElement value)
    {
        value = default;
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out value);
    }

    /// <summary>
    /// Tries to get report desc snapshot.
    /// </summary>
    /// <param name="uart">The uart.</param>
    /// <param name="itf">The itf.</param>
    /// <param name="snapshot">The snapshot.</param>
    /// <returns>Result.</returns>
    public static bool TryGetReportDescSnapshot(this HidUartClient uart, byte itf, out ReportDescriptorSnapshot snapshot)
    {
        snapshot = default!;
        ReportDescriptorSnapshot? snap = uart.GetReportDescriptorCopy(itf);
        if (snap is null) return false;
        snapshot = snap;
        return true;
    }

    /// <summary>
    /// Tries to get report desc hash.
    /// </summary>
    /// <param name="uart">The uart.</param>
    /// <param name="itf">The itf.</param>
    /// <param name="hash">The hash.</param>
    /// <returns>Result.</returns>
    public static bool TryGetReportDescHash(this HidUartClient uart, byte itf, out string? hash)
    {
        hash = null;
        ReportDescriptorSnapshot? snap = uart.GetReportDescriptorCopy(itf);
        if (snap is null || snap.Data.Length == 0) return false;
        hash = DeviceInterfaceMapper.ComputeReportDescHash(snap.Data);
        return true;
    }

    /// <summary>
    /// Tries to get mouse layout.
    /// </summary>
    /// <param name="uart">The uart.</param>
    /// <param name="itf">The itf.</param>
    /// <param name="layout">The layout.</param>
    /// <returns>Result.</returns>
    public static bool TryGetMouseLayout(this HidUartClient uart, byte itf, out MouseReportLayout layout)
    {
        return ReportLayoutService.TryGetMouseLayout(uart, itf, out layout);
    }

    /// <summary>
    /// Tries to get keyboard layout.
    /// </summary>
    /// <param name="uart">The uart.</param>
    /// <param name="itf">The itf.</param>
    /// <param name="layout">The layout.</param>
    /// <returns>Result.</returns>
    public static bool TryGetKeyboardLayout(this HidUartClient uart, byte itf, out KeyboardLayoutInfo layout)
    {
        return ReportLayoutService.TryGetKeyboardLayout(uart, itf, out layout);
    }

    /// <summary>
    /// Gets report descriptor.
    /// </summary>
    /// <param name="uart">The uart.</param>
    /// <param name="itf">The itf.</param>
    /// <param name="timeoutMs">The timeoutMs.</param>
    /// <param name="ct">The ct.</param>
    /// <returns>Result.</returns>
    public static Task<ReportDescriptorSnapshot?> GetReportDescriptorAsync(this HidUartClient uart, byte itf, int timeoutMs, CancellationToken ct)
    {
        return uart.RequestReportDescriptorAsync(itf, timeoutMs, ct);
    }

    /// <summary>
    /// Gets device id.
    /// </summary>
    /// <param name="uart">The uart.</param>
    /// <param name="timeoutMs">The timeoutMs.</param>
    /// <param name="ct">The ct.</param>
    /// <returns>Result.</returns>
    public static Task<byte[]?> GetDeviceIdAsync(this HidUartClient uart, int timeoutMs, CancellationToken ct)
    {
        return uart.RequestDeviceIdAsync(timeoutMs, ct);
    }

    /// <summary>
    /// Executes InjectMouseReportAsync.
    /// </summary>
    /// <param name="uart">The uart.</param>
    /// <param name="itfSel">The itfSel.</param>
    /// <param name="report">The report.</param>
    /// <param name="timeoutMs">The timeoutMs.</param>
    /// <param name="retries">The retries.</param>
    /// <param name="ct">The ct.</param>
    /// <returns>Result.</returns>
    public static Task InjectMouseReportAsync(this HidUartClient uart, byte itfSel, ReadOnlyMemory<byte> report, int timeoutMs, int retries, CancellationToken ct)
    {
        return uart.SendInjectReportAsync(itfSel, report, timeoutMs, retries, false, ct);
    }

    /// <summary>
    /// Executes InjectMouseReportAsync.
    /// </summary>
    /// <param name="uart">The uart.</param>
    /// <param name="itfSel">The itfSel.</param>
    /// <param name="report">The report.</param>
    /// <param name="ct">The ct.</param>
    /// <returns>Result.</returns>
    public static Task InjectMouseReportAsync(this HidUartClient uart, byte itfSel, ReadOnlyMemory<byte> report, CancellationToken ct)
    {
        return uart.SendInjectReportAsync(itfSel, report, ct);
    }

    /// <summary>
    /// Sets derived key.
    /// </summary>
    /// <param name="uart">The uart.</param>
    /// <param name="key">The key.</param>
    public static void SetDerivedKey(this HidUartClient uart, byte[] key)
    {
        uart.SetDerivedHmacKey(key);
    }

    /// <summary>
    /// Executes ForceBootstrap.
    /// </summary>
    /// <param name="uart">The uart.</param>
    public static void ForceBootstrap(this HidUartClient uart)
    {
        uart.UseBootstrapHmacKey();
    }
}
