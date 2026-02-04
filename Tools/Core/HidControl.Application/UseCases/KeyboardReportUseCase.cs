using HidControl.Application.Abstractions;
using HidControl.Application.Models;
using HidControl.Contracts;
using System.Collections.Generic;

namespace HidControl.Application.UseCases;

/// <summary>
/// Sends a raw keyboard report (modifiers + up to 6 keys) to the controlled device.
/// </summary>
public sealed class KeyboardReportUseCase
{
    private readonly IKeyboardControl _keyboard;

    /// <summary>
    /// Creates a new instance.
    /// </summary>
    public KeyboardReportUseCase(IKeyboardControl keyboard)
    {
        _keyboard = keyboard;
    }

    /// <summary>
    /// Executes the use case.
    /// </summary>
    public Task<KeyboardReportResult> ExecuteAsync(KeyboardReportRequest req, CancellationToken ct)
    {
        byte modifiers = req.Modifiers ?? 0;
        int[] keys = req.Keys ?? Array.Empty<int>();
        if (keys.Length > 6)
        {
            return Task.FromResult(new KeyboardReportResult(false, "keys_max_6", req.ItfSel, 0, Array.Empty<byte>()));
        }

        var keyBytes = new List<byte>(keys.Length);
        foreach (int key in keys)
        {
            if (key < 0 || key > 255)
            {
                return Task.FromResult(new KeyboardReportResult(false, "keys_out_of_range", req.ItfSel, 0, Array.Empty<byte>()));
            }
            keyBytes.Add((byte)key);
        }

        bool applyMapping = req.ApplyMapping ?? true;
        return _keyboard.SendReportAsync(modifiers, keyBytes, applyMapping, req.ItfSel, ct);
    }
}
