using System;
using System.Linq;
using System.Text.Json;
using HidControl.Core;
using HidControlServer;

namespace HidControlServer.Services;

/// <summary>
/// Resolves HID interface selections and cached device interfaces.
/// </summary>
internal static class InterfaceSelector
{
    /// <summary>
    /// Resolves the interface selector based on request, type, and fallback.
    /// </summary>
    /// <param name="requestItfSel">Requested interface selector.</param>
    /// <param name="typeName">Requested device type.</param>
    /// <param name="fallbackItfSel">Fallback interface selector.</param>
    /// <param name="uart">UART client.</param>
    /// <returns>Resolved interface selector.</returns>
    public static byte ResolveItfSel(byte? requestItfSel, string? typeName, byte fallbackItfSel, HidUartClient uart)
    {
        if (requestItfSel is not null)
        {
            if (requestItfSel.Value == 0xFF)
            {
                return ResolveItfByType(uart, "mouse", fallbackItfSel);
            }
            if (requestItfSel.Value == 0xFE)
            {
                return ResolveItfByType(uart, "keyboard", fallbackItfSel);
            }
            return requestItfSel.Value;
        }
        if (!string.IsNullOrWhiteSpace(typeName))
        {
            return ResolveItfByType(uart, typeName, fallbackItfSel);
        }
        return fallbackItfSel;
    }

    /// <summary>
    /// Resolves the interface selector and refreshes interface list if needed.
    /// </summary>
    /// <param name="requestItfSel">Requested interface selector.</param>
    /// <param name="typeName">Requested device type.</param>
    /// <param name="fallbackItfSel">Fallback interface selector.</param>
    /// <param name="uart">UART client.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Resolved interface selector.</returns>
    public static async Task<byte> ResolveItfSelAsync(byte? requestItfSel, string? typeName, byte fallbackItfSel, HidUartClient uart, CancellationToken ct)
    {
        byte itfSel = ResolveItfSel(requestItfSel, typeName, fallbackItfSel, uart);
        if (itfSel == 0xFF || itfSel == 0xFE)
        {
            try
            {
                await uart.RequestInterfaceListAsync(500, ct);
            }
            catch
            {
                try
                {
                    await uart.RequestInterfaceListAsync(500, true, ct);
                    uart.UseBootstrapHmacKey();
                }
                catch { }
            }
            itfSel = ResolveItfSel(requestItfSel, typeName, fallbackItfSel, uart);
        }
        return itfSel;
    }

    /// <summary>
    /// Tries to resolve an interface selector from the cached devices document.
    /// </summary>
    /// <param name="state">Application state.</param>
    /// <param name="typeName">Device type name.</param>
    /// <param name="itfSel">Resolved interface selector.</param>
    /// <returns>True if resolved.</returns>
    public static bool TryResolveItfSelFromCache(AppState state, string typeName, out byte itfSel)
    {
        itfSel = 0xFF;
        if (state.CachedDevicesDetailDoc is null) return false;
        JsonElement root = state.CachedDevicesDetailDoc.RootElement;
        if (root.TryGetProperty("list", out var listEl))
        {
            root = listEl;
        }
        if (!root.TryGetProperty("interfaces", out var itfs) || itfs.ValueKind != JsonValueKind.Array)
        {
            return false;
        }
        foreach (JsonElement itf in itfs.EnumerateArray())
        {
            if (!itf.TryGetProperty("typeName", out var typeEl)) continue;
            string? name = typeEl.GetString();
            if (!string.Equals(name, typeName, StringComparison.OrdinalIgnoreCase)) continue;
            if (itf.TryGetProperty("itf", out var itfEl) && itfEl.TryGetByte(out byte val))
            {
                itfSel = val;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Returns active interface IDs from the last interface list snapshot.
    /// </summary>
    /// <param name="uart">UART client.</param>
    /// <returns>Active interface selectors.</returns>
    public static IReadOnlyList<byte> GetActiveItfs(HidUartClient uart)
    {
        HidInterfaceList? list = uart.GetLastInterfaceList();
        if (list is null) return Array.Empty<byte>();
        return list.Interfaces.Where(i => i.Active && i.Mounted).Select(i => i.Itf).ToArray();
    }

    private static byte ResolveItfByType(HidUartClient uart, string typeName, byte fallbackItfSel)
    {
        HidInterfaceList? list = uart.GetLastInterfaceList();
        if (list is not null)
        {
            HidInterfaceInfo? match = list.Interfaces.FirstOrDefault(i =>
                string.Equals(DeviceInterfaceMapper.InferTypeName(i.InferredType, i.ItfProtocol), typeName, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match.Itf;
        }
        return fallbackItfSel;
    }
}
