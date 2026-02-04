using HidControl.Application.Abstractions;
using HidControl.Application.Models;
using HidControlServer.Services;

namespace HidControlServer.Adapters.Application;

/// <summary>
/// Server-side implementation of <see cref="IMouseControl"/> using the UART transport.
/// </summary>
public sealed class MouseControlAdapter : IMouseControl
{
    private readonly Options _opt;
    private readonly HidUartClient _uart;
    private readonly AppState _appState;
    private readonly MouseState _mouseState;
    private readonly MouseMappingStore _mouseMapping;

    /// <summary>
    /// Creates a new instance.
    /// </summary>
    public MouseControlAdapter(Options opt, HidUartClient uart, AppState appState, MouseState mouseState, MouseMappingStore mouseMapping)
    {
        _opt = opt;
        _uart = uart;
        _appState = appState;
        _mouseState = mouseState;
        _mouseMapping = mouseMapping;
    }

    /// <inheritdoc />
    public async Task<MouseMoveResult> MoveAsync(int dx, int dy, int wheel, byte? itfSel, CancellationToken ct)
    {
        if (!_opt.MouseMoveAllowZero && dx == 0 && dy == 0 && wheel == 0)
        {
            return new MouseMoveResult(true, null, Skipped: true, ItfSel: itfSel);
        }

        byte resolved = await ResolveMouseItfSelAsync(itfSel, ct);
        if (resolved == 0xFF)
        {
            return new MouseMoveResult(false, "mouse_itf_unresolved", false, null);
        }

        byte buttons = _mouseState.GetButtons();
        await ReportLayoutService.EnsureMouseLayoutAsync(_uart, resolved, ct);
        byte[] report = InputReportBuilder.TryBuildMouseReport(_uart, resolved, buttons, dx, dy, wheel, _opt.MouseReportLen);
        await _uart.SendInjectReportAsync(resolved, report, _opt.MouseMoveTimeoutMs, 0, _opt.MouseMoveDropIfBusy, ct);

        return new MouseMoveResult(true, null, Skipped: false, ItfSel: resolved);
    }

    /// <inheritdoc />
    public async Task<MouseWheelResult> WheelAsync(int delta, byte? itfSel, CancellationToken ct)
    {
        if (!_opt.MouseWheelAllowZero && delta == 0)
        {
            return new MouseWheelResult(true, null, Skipped: true, ItfSel: itfSel);
        }

        byte resolved = await ResolveMouseItfSelAsync(itfSel, ct);
        if (resolved == 0xFF)
        {
            return new MouseWheelResult(false, "mouse_itf_unresolved", false, null);
        }

        await ReportLayoutService.EnsureMouseLayoutAsync(_uart, resolved, ct);
        byte[] report = InputReportBuilder.TryBuildMouseReport(_uart, resolved, _mouseState.GetButtons(), 0, 0, delta, _opt.MouseReportLen);
        await _uart.SendInjectReportAsync(resolved, report, _opt.MouseWheelTimeoutMs, 0, _opt.MouseWheelDropIfBusy, ct);
        return new MouseWheelResult(true, null, Skipped: false, ItfSel: resolved);
    }

    /// <inheritdoc />
    public async Task<MouseButtonResult> SetButtonAsync(string button, bool down, byte? itfSel, CancellationToken ct)
    {
        byte resolved = await ResolveMouseItfSelAsync(itfSel, ct);
        if (resolved == 0xFF)
        {
            return new MouseButtonResult(false, "mouse_itf_unresolved", null, _mouseState.GetButtons());
        }

        byte mask = InputReportBuilder.ResolveMouseButtonMask(button ?? string.Empty, _opt, _uart, _mouseMapping, resolved);
        InputReportBuilder.ApplyMouseButtonMask(_mouseState, mask, down);

        await ReportLayoutService.EnsureMouseLayoutAsync(_uart, resolved, ct);
        byte[] report = InputReportBuilder.TryBuildMouseReport(_uart, resolved, _mouseState.GetButtons(), 0, 0, 0, _opt.MouseReportLen);
        await _uart.SendInjectReportAsync(resolved, report, _opt.InjectTimeoutMs, _opt.InjectRetries, false, ct);

        return new MouseButtonResult(true, null, resolved, _mouseState.GetButtons());
    }

    /// <inheritdoc />
    public async Task<MouseButtonsMaskResult> SetButtonsMaskAsync(byte mask, byte? itfSel, CancellationToken ct)
    {
        _mouseState.SetButtonsMask(mask);

        byte resolved = await ResolveMouseItfSelAsync(itfSel, ct);
        if (resolved == 0xFF)
        {
            return new MouseButtonsMaskResult(false, "mouse_itf_unresolved", null, _mouseState.GetButtons());
        }

        await ReportLayoutService.EnsureMouseLayoutAsync(_uart, resolved, ct);
        byte[] report = InputReportBuilder.TryBuildMouseReport(_uart, resolved, _mouseState.GetButtons(), 0, 0, 0, _opt.MouseReportLen);
        await _uart.SendInjectReportAsync(resolved, report, _opt.InjectTimeoutMs, _opt.InjectRetries, false, ct);

        return new MouseButtonsMaskResult(true, null, resolved, _mouseState.GetButtons());
    }

    private Task<byte> ResolveMouseItfSelAsync(byte? requestItfSel, CancellationToken ct)
    {
        string typeName = _opt.MouseTypeName ?? "mouse";
        if (requestItfSel is null && InterfaceSelector.TryResolveItfSelFromCache(_appState, typeName, out byte cached))
        {
            return Task.FromResult(cached);
        }

        byte itfSel = InterfaceSelector.ResolveItfSel(requestItfSel, typeName, _opt.MouseItfSel, _uart);
        if (itfSel != 0xFF)
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

