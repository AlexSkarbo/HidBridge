using HidControl.Application.Abstractions;
using HidControl.Application.Models;
using HidControl.Core;
using HidControlServer.Services;

namespace HidControlServer.Adapters.Application;

/// <summary>
/// Server-side implementation of <see cref="IKeyboardControl"/> using the UART transport.
/// </summary>
public sealed class KeyboardControlAdapter : IKeyboardControl
{
    private readonly Options _opt;
    private readonly HidUartClient _uart;
    private readonly AppState _appState;
    private readonly KeyboardMappingStore _keyboardMapping;

    /// <summary>
    /// Creates a new instance.
    /// </summary>
    public KeyboardControlAdapter(Options opt, HidUartClient uart, AppState appState, KeyboardMappingStore keyboardMapping)
    {
        _opt = opt;
        _uart = uart;
        _appState = appState;
        _keyboardMapping = keyboardMapping;
    }

    /// <inheritdoc />
    public async Task<KeyboardPressResult> PressAsync(byte usage, byte modifiers, byte? itfSel, CancellationToken ct)
    {
        byte resolved = await ResolveKeyboardItfSelAsync(itfSel, ct);
        if (resolved == 0xFE)
        {
            return new KeyboardPressResult(false, "keyboard_itf_unresolved", null);
        }

        await ReportLayoutService.EnsureReportLayoutAsync(_uart, resolved, ct);

        byte mappedUsage = InputReportBuilder.ResolveKeyboardUsage(_uart, _keyboardMapping, resolved, usage);
        if (InputReportBuilder.IsModifierUsage(mappedUsage))
        {
            byte bit = InputReportBuilder.ModifierBit(mappedUsage);
            byte[] downReportMod = InputReportBuilder.TryBuildKeyboardReport(_uart, resolved, (byte)(modifiers | bit), Array.Empty<byte>());
            byte[] upReportMod = InputReportBuilder.TryBuildKeyboardReport(_uart, resolved, (byte)(modifiers & ~bit), Array.Empty<byte>());
            try
            {
                await _uart.SendInjectReportAsync(resolved, downReportMod, _opt.KeyboardInjectTimeoutMs, 0, false, ct);
                await _uart.SendInjectReportAsync(resolved, upReportMod, _opt.KeyboardInjectTimeoutMs, 0, false, ct);
            }
            catch (Exception ex)
            {
                return new KeyboardPressResult(false, ex.Message, resolved);
            }

            return new KeyboardPressResult(true, null, resolved);
        }

        byte[] downReport = InputReportBuilder.TryBuildKeyboardReport(_uart, resolved, modifiers, new[] { mappedUsage });
        byte[] upReport = InputReportBuilder.TryBuildKeyboardReport(_uart, resolved, 0, Array.Empty<byte>());
        try
        {
            await _uart.SendInjectReportAsync(resolved, downReport, _opt.KeyboardInjectTimeoutMs, 0, false, ct);
            await _uart.SendInjectReportAsync(resolved, upReport, _opt.KeyboardInjectTimeoutMs, 0, false, ct);
        }
        catch (Exception ex)
        {
            return new KeyboardPressResult(false, ex.Message, resolved);
        }

        return new KeyboardPressResult(true, null, resolved);
    }

    /// <inheritdoc />
    public async Task<KeyboardTextResult> TypeTextAsync(string text, string? layout, byte? itfSel, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(text))
        {
            return new KeyboardTextResult(true, null, itfSel, null);
        }

        byte resolved = await ResolveKeyboardItfSelAsync(itfSel, ct);
        if (resolved == 0xFE)
        {
            return new KeyboardTextResult(false, "keyboard_itf_unresolved", null, null);
        }

        await ReportLayoutService.EnsureReportLayoutAsync(_uart, resolved, ct);

        foreach (char ch in text)
        {
            if (!HidReports.TryMapTextToHidKey(ch, layout, out byte chMods, out byte chUsage))
            {
                return new KeyboardTextResult(false, $"unsupported_char_{(int)ch:X4}", resolved, ch);
            }

            byte mapped = InputReportBuilder.ResolveKeyboardUsage(_uart, _keyboardMapping, resolved, chUsage);
            if (InputReportBuilder.IsModifierUsage(mapped))
            {
                byte bit = InputReportBuilder.ModifierBit(mapped);
                byte[] downReportMod = InputReportBuilder.TryBuildKeyboardReport(_uart, resolved, (byte)(chMods | bit), Array.Empty<byte>());
                byte[] upReportMod = InputReportBuilder.TryBuildKeyboardReport(_uart, resolved, (byte)(chMods & ~bit), Array.Empty<byte>());
                await _uart.SendInjectReportAsync(resolved, downReportMod, _opt.KeyboardInjectTimeoutMs, 0, false, ct);
                await _uart.SendInjectReportAsync(resolved, upReportMod, _opt.KeyboardInjectTimeoutMs, 0, false, ct);
                continue;
            }

            byte[] downReport = InputReportBuilder.TryBuildKeyboardReport(_uart, resolved, chMods, new[] { mapped });
            byte[] upReport = InputReportBuilder.TryBuildKeyboardReport(_uart, resolved, 0, Array.Empty<byte>());
            await _uart.SendInjectReportAsync(resolved, downReport, _opt.KeyboardInjectTimeoutMs, 0, false, ct);
            await _uart.SendInjectReportAsync(resolved, upReport, _opt.KeyboardInjectTimeoutMs, 0, false, ct);
        }

        return new KeyboardTextResult(true, null, resolved, null);
    }

    /// <inheritdoc />
    public async Task<KeyboardShortcutResult> SendShortcutAsync(string shortcut, int holdMs, bool applyMapping, byte? itfSel, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(shortcut))
        {
            return new KeyboardShortcutResult(false, "shortcut_required", null, 0, Array.Empty<byte>());
        }

        byte resolved = await ResolveKeyboardItfSelAsync(itfSel, ct);
        if (resolved == 0xFE)
        {
            return new KeyboardShortcutResult(false, "keyboard_itf_unresolved", null, 0, Array.Empty<byte>());
        }

        if (!HidKeyboardShortcuts.TryParseChord(shortcut, out byte parsedMods, out byte[] parsedKeys, out string? parseErr))
        {
            return new KeyboardShortcutResult(false, parseErr ?? "invalid_shortcut", resolved, 0, Array.Empty<byte>());
        }

        byte finalMods = 0;
        var finalKeys = new List<byte>(Math.Min(6, parsedKeys.Length));

        if (applyMapping)
        {
            // Map modifier bits as individual usages (0xE0..0xE7) through the per-device mapping store.
            for (int i = 0; i < 8; i++)
            {
                byte bit = (byte)(1 << i);
                if ((parsedMods & bit) == 0) continue;
                byte usage = (byte)(0xE0 + i);
                byte mapped = InputReportBuilder.ResolveKeyboardUsage(_uart, _keyboardMapping, resolved, usage);
                if (InputReportBuilder.IsModifierUsage(mapped))
                {
                    finalMods = (byte)(finalMods | InputReportBuilder.ModifierBit(mapped));
                    continue;
                }
                if (!finalKeys.Contains(mapped))
                {
                    finalKeys.Add(mapped);
                    if (finalKeys.Count > 6)
                    {
                        return new KeyboardShortcutResult(false, "keys_max_6", resolved, 0, Array.Empty<byte>());
                    }
                }
            }

            foreach (byte usage in parsedKeys)
            {
                byte mapped = InputReportBuilder.ResolveKeyboardUsage(_uart, _keyboardMapping, resolved, usage);
                if (InputReportBuilder.IsModifierUsage(mapped))
                {
                    finalMods = (byte)(finalMods | InputReportBuilder.ModifierBit(mapped));
                    continue;
                }
                if (!finalKeys.Contains(mapped))
                {
                    finalKeys.Add(mapped);
                    if (finalKeys.Count > 6)
                    {
                        return new KeyboardShortcutResult(false, "keys_max_6", resolved, 0, Array.Empty<byte>());
                    }
                }
            }

            // Keep behavior consistent with existing endpoints: do not "promote" mapped modifier usages into the modifier bitmask here.
            // If needed, this can be enabled later behind an explicit flag.
        }
        else
        {
            finalMods = parsedMods;
            foreach (byte keyUsage in parsedKeys)
            {
                if (finalKeys.Contains(keyUsage)) continue;
                finalKeys.Add(keyUsage);
                if (finalKeys.Count > 6)
                {
                    return new KeyboardShortcutResult(false, "keys_max_6", resolved, 0, Array.Empty<byte>());
                }
            }
        }

        await ReportLayoutService.EnsureReportLayoutAsync(_uart, resolved, ct);
        byte[] downReport = InputReportBuilder.TryBuildKeyboardReport(_uart, resolved, finalMods, finalKeys);
        byte[] upReport = InputReportBuilder.TryBuildKeyboardReport(_uart, resolved, 0, Array.Empty<byte>());

        await _uart.SendInjectReportAsync(resolved, downReport, _opt.KeyboardInjectTimeoutMs, 0, false, ct);
        if (holdMs > 0)
        {
            await Task.Delay(holdMs, ct);
        }
        await _uart.SendInjectReportAsync(resolved, upReport, _opt.KeyboardInjectTimeoutMs, 0, false, ct);

        return new KeyboardShortcutResult(true, null, resolved, finalMods, finalKeys);
    }

    private Task<byte> ResolveKeyboardItfSelAsync(byte? requestItfSel, CancellationToken ct)
    {
        string typeName = _opt.KeyboardTypeName ?? "keyboard";
        if (requestItfSel is null && InterfaceSelector.TryResolveItfSelFromCache(_appState, typeName, out byte cached))
        {
            return Task.FromResult(cached);
        }

        byte itfSel = InterfaceSelector.ResolveItfSel(requestItfSel, typeName, _opt.KeyboardItfSel, _uart);
        if (itfSel != 0xFE)
        {
            return Task.FromResult(itfSel);
        }

        if (InterfaceSelector.TryResolveItfSelFromCache(_appState, typeName, out cached))
        {
            return Task.FromResult(cached);
        }

        return Task.FromResult(itfSel);
    }
}
